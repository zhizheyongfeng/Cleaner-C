using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// ============================================================
// MFT 直读原型（spike）—— 验证 Cleaner-C 采用 MFT 加速的可行性
// 目的：把"预计秒级"变成实测数字，并验证物理/逻辑双口径
// 用法（需管理员）：mftspike.exe <输出文件路径>
// ============================================================

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}

sealed class Volume : IDisposable
{
    readonly IntPtr h;
    const int SECTOR = 512;
    byte[] tmp = new byte[20 * 1024 * 1024];

    public Volume(string devicePath)
    {
        h = Native.CreateFileW(devicePath, 0x80000000u, 0x7u, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h == new IntPtr(-1))
            throw new IOException("CreateFile(" + devicePath + ") 失败, Win32Error=" + Marshal.GetLastWin32Error());
    }

    // 从卷偏移读取 count 字节到 buf[0..count)。
    // 卷句柄要求【按扇区对齐】的偏移与长度，否则 ReadFile 返回 err=87，
    // 因此这里向下取整偏移、向上取整长度，读完后截取所需部分。
    public void ReadAt(long offset, byte[] buf, int count)
    {
        long alignedOff = offset - (offset % SECTOR);
        int prefix = (int)(offset - alignedOff);
        int total = prefix + count;
        int alignedTotal = ((total + SECTOR - 1) / SECTOR) * SECTOR;
        if (alignedTotal > tmp.Length)
            throw new IOException("单次读取过大: " + alignedTotal);

        long np;
        if (!Native.SetFilePointerEx(h, alignedOff, out np, 0))
            throw new IOException("SetFilePointerEx 失败 err=" + Marshal.GetLastWin32Error());

        uint got;
        if (!Native.ReadFile(h, tmp, (uint)alignedTotal, out got, IntPtr.Zero))
            throw new IOException("ReadFile 失败 err=" + Marshal.GetLastWin32Error());

        int avail = (int)got - prefix;
        if (avail > count) avail = count;
        if (avail < 0) avail = 0;
        if (avail > 0) Array.Copy(tmp, prefix, buf, 0, avail);
        if (avail != count) throw new IOException("短读: 期望 " + count + " 实得 " + avail);
    }

    public void Dispose()
    {
        if (h != new IntPtr(-1)) Native.CloseHandle(h);
    }
}

sealed class Run
{
    public long Lcn;
    public long Clusters;
}

static class Program
{
    const uint ATTR_STANDARD_INFORMATION = 0x10;
    const uint ATTR_FILE_NAME = 0x30;
    const uint ATTR_DATA = 0x80;
    const uint ATTR_END = 0xFFFFFFFF;
    const ulong ROOT_FRN = 5;

    // 归属失败诊断
    static long cntParentZero = 0, cntNameNull = 0;
    static readonly Dictionary<long, long> unresStop = new Dictionary<long, long>();
    static void Record(long idx) { long c; unresStop.TryGetValue(idx, out c); unresStop[idx] = c + 1; }

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "mft-report.txt";
        var log = new StringBuilder();
        var swTotal = Stopwatch.StartNew();

        void W(string s) { log.AppendLine(s); Console.WriteLine(s); }

        try
        {
            W("=== MFT 直读原型 (Cleaner-C spike) ===");
            W("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            W("管理员: " + IsAdmin());
            W("");

            using (var vol = new Volume(@"\\.\C:"))
            {
                // ---- 1) 解析引导扇区 ----
                var boot = new byte[512];
                vol.ReadAt(0, boot, 512);
                string oem = Encoding.ASCII.GetString(boot, 3, 8);
                int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
                int sectorsPerCluster = boot[0x0D];
                long clusterSize = (long)bytesPerSector * sectorsPerCluster;
                long mftLcn = BitConverter.ToInt64(boot, 0x30);
                // 注意：这两个字段在 NTFS 引导扇区中是【有符号单字节】
                // 0xF6 = -10  =>  记录大小 = 1 << 10 = 1024 字节（不是 246！）
                sbyte recSizeRaw = (sbyte)boot[0x40];
                int recordSize = recSizeRaw > 0 ? recSizeRaw : (recSizeRaw < 0 ? 1 << (-recSizeRaw) : 1024);
                sbyte idxSizeRaw = (sbyte)boot[0x44];
                int indexSize = idxSizeRaw > 0 ? idxSizeRaw : (idxSizeRaw < 0 ? 1 << (-idxSizeRaw) : 4096);

                W("OEM 标识           : " + oem);
                W("每扇区字节         : " + bytesPerSector);
                W("每簇扇区           : " + sectorsPerCluster + "  => 簇大小 " + clusterSize + " 字节");
                W("MFT 起始簇         : " + mftLcn + "  => 偏移 " + (mftLcn * clusterSize));
                W("MFT 记录大小       : " + recordSize + " 字节");
                W("");

                if (!oem.StartsWith("NTFS")) { W("非 NTFS 卷，退出"); return 2; }

                // ---- 2) 读取 MFT 记录 0，取出 $MFT 自身的 $DATA 数据运行 ----
                var rec0 = new byte[recordSize];
                vol.ReadAt(mftLcn * clusterSize, rec0, recordSize);
                if (Encoding.ASCII.GetString(rec0, 0, 4) != "FILE") { W("记录 0 不是 FILE 记录，退出"); return 3; }

                var runs = new List<Run>();
                long mftDataSize = ParseMftRunList(rec0, recordSize, ref runs);

                long runBytesTotal = 0;
                foreach (var r in runs) runBytesTotal += r.Clusters * clusterSize;

                W("$MFT 数据运行数    : " + runs.Count);
                W("$MFT 数据大小      : " + mftDataSize + " 字节 (" + (mftDataSize / 1048576.0).ToString("F1") + " MB)");
                W("$MFT 已分配        : " + runBytesTotal + " 字节 (" + (runBytesTotal / 1048576.0).ToString("F1") + " MB)");
                W("");

                long recordCount = mftDataSize / recordSize;
                W("MFT 记录总数       : " + recordCount);
                W("");

                // ---- 3) 顺序流式读取全部 MFT 记录并解析 ----
                var sw = Stopwatch.StartNew();

                var inUse = new bool[recordCount];
                var isDir = new bool[recordCount];
                var logicalSize = new long[recordCount];
                var allocSize = new long[recordCount];
                var primaryParent = new ulong[recordCount];
                var nameCount = new int[recordCount];
                var realNameCount = new int[recordCount];   // 命名空间 != 2 的名字项（排除 DOS 8.3 别名）
                var primaryName = new string[recordCount];
                var logicalOnly = new long[recordCount];   // 被多个父目录共享的文件：记录其名字项数

                long recsRead = 0, files = 0, dirs = 0, bytesLogicalOnce = 0, bytesAllocated = 0;
                long bytesAllNames = 0;    // 逻辑口径：每个 FILE_NAME 项都计入（含 DOS 别名）
                long bytesRealNames = 0;   // 逻辑口径：仅计入真实名字项（ns != 2）
                long hardLinkRecs = 0, reparseCount = 0, nameEntriesTotal = 0;
                long dosAliasEntries = 0, realNameEntriesTotal = 0;
                long parseErrors = 0;

                int chunkRecords = Math.Max(1, (16 * 1024 * 1024) / recordSize);
                var buf = new byte[(long)chunkRecords * recordSize > int.MaxValue ? int.MaxValue : chunkRecords * recordSize];

                long idx = 0;
                while (idx < recordCount)
                {
                    int take = (int)Math.Min(chunkRecords, recordCount - idx);
                    int bytes = take * recordSize;
                    ReadMftData(vol, runs, clusterSize, idx * (long)recordSize, buf, bytes);

                    for (int k = 0; k < take; k++)
                    {
                        int off = k * recordSize;
                        recsRead++;

                        if (Encoding.ASCII.GetString(buf, off, 4) != "FILE") continue;
                        ushort flags = BitConverter.ToUInt16(buf, off + 0x16);
                        if ((flags & 0x01) == 0) continue;      // 未使用

                        // 应用 update sequence array 修复
                        ApplyFixup(buf, off, recordSize, bytesPerSector);

                        long recIdx = idx + k;
                        inUse[recIdx] = true;
                        bool thisIsDir = (flags & 0x02) != 0;
                        isDir[recIdx] = thisIsDir;

                        int attrOff = off + BitConverter.ToUInt16(buf, off + 0x14);
                        int limit = off + recordSize;
                        string bestName = null;
                        int bestNs = 99;
                        ulong firstParent = 0;
                        int nNames = 0;
                        int realNames = 0;
                        ulong realParent = 0;
                        long dataLogical = 0, dataAlloc = 0;
                        bool hasData = false;

                        while (attrOff + 4 <= limit)
                        {
                            uint atype = BitConverter.ToUInt32(buf, attrOff);
                            if (atype == ATTR_END) break;
                            int alen = (int)BitConverter.ToUInt32(buf, attrOff + 4);
                            if (alen <= 0 || attrOff + alen > limit) { parseErrors++; break; }

                            if (atype == ATTR_FILE_NAME)
                            {
                                bool resident = buf[attrOff + 8] == 0;
                                if (resident)
                                {
                                    int voff = BitConverter.ToUInt16(buf, attrOff + 0x14);
                                    ulong parent = BitConverter.ToUInt64(buf, attrOff + voff) & 0x0000FFFFFFFFFFFFUL;
                                    int nameLen = buf[attrOff + voff + 0x40];
                                    int ns = buf[attrOff + voff + 0x41];
                                    bool isDosOnly = (ns == 2);          // 纯 8.3 短名别名，不是独立的用户可见条目
                                    if (!isDosOnly)
                                    {
                                        realNames++;
                                        if (realParent == 0) realParent = parent;
                                    }
                                    if (nNames == 0) firstParent = parent;
                                    nNames++;
                                    if (!isDosOnly && (bestName == null || ns < bestNs))
                                    {
                                        bestNs = ns;
                                        if (attrOff + voff + 0x42 + nameLen * 2 <= limit)
                                            bestName = Encoding.Unicode.GetString(buf, attrOff + voff + 0x42, nameLen * 2);
                                    }
                                }
                            }
                            else if (atype == ATTR_DATA)
                            {
                                int nl = buf[attrOff + 9];
                                bool resident = buf[attrOff + 8] == 0;
                                if (nl == 0)   // 匿名数据流
                                {
                                    if (resident) { dataLogical = BitConverter.ToUInt32(buf, attrOff + 0x10); dataAlloc = dataLogical; hasData = true; }
                                    else { dataAlloc = BitConverter.ToInt64(buf, attrOff + 0x28); dataLogical = BitConverter.ToInt64(buf, attrOff + 0x30); hasData = true; }
                                }
                            }
                            else if (atype == 0xC0)  // REPARSE_POINT
                            {
                                reparseCount++;
                            }

                            attrOff += alen;
                        }

                        // 父目录优先取"真实名字项"的父；仅当记录只有 DOS 别名时才退回任意项
                        primaryParent[recIdx] = (realParent != 0) ? realParent : firstParent;
                        primaryName[recIdx] = bestName;
                        nameCount[recIdx] = nNames;
                        realNameCount[recIdx] = realNames;
                        nameEntriesTotal += nNames;
                        realNameEntriesTotal += realNames;
                        dosAliasEntries += (nNames - realNames);

                        if (thisIsDir) dirs++;
                        else
                        {
                            files++;
                            logicalSize[recIdx] = dataLogical;
                            allocSize[recIdx] = dataAlloc;
                            bytesLogicalOnce += dataLogical;       // 物理口径（每个 MFT 记录只算一次）
                            bytesAllocated += dataAlloc;
                            bytesAllNames += dataLogical * (nNames > 0 ? nNames : 1);        // 含 DOS 别名（会虚高）
                            bytesRealNames += dataLogical * (realNames > 0 ? realNames : 1); // 仅真实名字项
                            if (realNames > 1) hardLinkRecs++;
                            if (hasData) { }
                        }
                    }

                    idx += take;
                    if ((idx % (recordCount / 10 == 0 ? 1 : recordCount / 10)) < chunkRecords)
                        Console.WriteLine("  进度: " + (idx * 100 / recordCount) + "% (" + idx + "/" + recordCount + ")");
                }

                sw.Stop();
                W("--- 解析完成 ---");
                W("耗时               : " + sw.ElapsedMilliseconds + " ms");
                W("处理记录           : " + recsRead);
                W("使用中记录         : " + (files + dirs));
                W("  文件             : " + files);
                W("  目录             : " + dirs);
                W("重解析点(REPARSE)  : " + reparseCount);
                W("解析错误           : " + parseErrors);
                W("");

                W("--- 口径对比（核心验证）---");
                W("物理口径(每记录一次) : " + GB(bytesLogicalOnce) + " GB");
                W("逻辑口径(仅真实名字) : " + GB(bytesRealNames) + " GB");
                W("逻辑口径(含DOS别名)  : " + GB(bytesAllNames) + " GB");
                W("差额(真实逻辑-物理)  : " + GB(bytesRealNames - bytesLogicalOnce) + " GB"
                  + (bytesRealNames > 0 ? "  (" + ((bytesRealNames - bytesLogicalOnce) * 100.0 / bytesRealNames).ToString("F1") + "%)" : ""));
                W("已分配(含稀疏)      : " + GB(bytesAllocated) + " GB");
                W("硬链接文件数(名字项>1): " + hardLinkRecs);
                W("名字项总数          : " + nameEntriesTotal + "  (真实 " + realNameEntriesTotal + " + DOS别名 " + dosAliasEntries + ")");
                // ---- 4) 顶层目录聚合（对比 Node 结果）----
                W("--- 顶层目录（物理口径，按 MFT 记录归属主父目录）---");
                var topPhys = new Dictionary<string, long>();
                var topLogical = new Dictionary<string, long>();
                var topPhysBytes = new Dictionary<string, long>();
                var topAnc = new string[recordCount];

                for (long i = 0; i < recordCount; i++)
                {
                    if (!inUse[i] || isDir[i]) continue;
                    string top = ResolveTop(i, inUse, isDir, primaryParent, primaryName, topAnc, recordCount);
                    if (top == null) top = "(未归属)";
                    if (!topPhys.ContainsKey(top)) { topPhys[top] = 0; topLogical[top] = 0; }
                    topPhys[top] += logicalSize[i];
                    topLogical[top] += logicalSize[i] * (realNameCount[i] > 0 ? realNameCount[i] : 1);
                }

                var sorted = new List<KeyValuePair<string, long>>(topPhys);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var kv in sorted)
                    W("  " + GB(kv.Value).PadLeft(10) + " GB   逻辑 " + GB(topLogical[kv.Key]).PadLeft(10) + " GB   " + kv.Key);


                W("");
                W("--- 归属失败诊断（在聚合循环之后统计）---");
                W("父为0的记录数        : " + cntParentZero);
                W("顶层名为空的记录数    : " + cntNameNull);
                W("未归属停止点 TOP 10   : ");
                var us = new List<KeyValuePair<long, long>>(unresStop);
                us.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int q = 0; q < Math.Min(10, us.Count); q++)
                {
                    long si = us[q].Key;
                    string snm = (si >= 0 && si < recordCount) ? primaryName[si] : "?";
                    W("   索引 " + si + " 次数 " + us[q].Value + " 名称 " + (snm ?? "(null)"));
                }
                W("");
                W("总耗时(含读取+解析): " + swTotal.ElapsedMilliseconds + " ms");
            }

            File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("报告已写入: " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            W("异常: " + ex.GetType().Name + ": " + ex.Message);
            try { File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false)); } catch { }
            return 1;
        }
    }
    static string GB(long bytes) { return (bytes / 1073741824.0).ToString("F2"); }

    // 从某个记录向上走父链，直到父目录是根目录(FRN=5)；返回那一层的名字
    static string ResolveTop(long rec, bool[] inUse, bool[] isDir, ulong[] parent, string[] name,
                             string[] cache, long recordCount)
    {
        long cur = rec;
        int guard = 0;
        while (guard++ < 4096)
        {
            if (cur < 0 || cur >= recordCount) { Record(cur); return null; }
            ulong p = parent[cur];
            long pi = (long)(p & 0x0000FFFFFFFFFFFFUL);
            if (pi == 0) { cntParentZero++; Record(cur); return null; }   // 未解析出父目录 → 不归属
            if (pi == (long)ROOT_FRN)
            {
                string nm = name[cur];
                if (nm == null) { cntNameNull++; Record(cur); }
                return nm;
            }
            if (pi == cur || pi < 0 || pi >= recordCount) { Record(cur); return null; }
            if (!inUse[pi]) { Record(pi); return null; }
            cur = pi;
        }
        Record(cur);   // guard 用尽（父链成环）
        return null;
    }

    static bool IsAdmin()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    static void ApplyFixup(byte[] buf, int recOff, int recordSize, int bytesPerSector)
    {
        int usaOff = BitConverter.ToUInt16(buf, recOff + 0x04);
        int usaCnt = BitConverter.ToUInt16(buf, recOff + 0x06);
        if (usaCnt <= 1) return;
        ushort sig = BitConverter.ToUInt16(buf, recOff + usaOff);
        int sectors = recordSize / bytesPerSector;
        for (int s = 0; s < sectors; s++)
        {
            int pos = recOff + s * bytesPerSector + bytesPerSector - 2;
            if (pos + 2 > recOff + recordSize) break;
            if (BitConverter.ToUInt16(buf, pos) == sig && s + 1 < usaCnt)
                Buffer.BlockCopy(buf, recOff + usaOff + (s + 1) * 2, buf, pos, 2);
        }
    }

    // 从 MFT 记录 0 的 $DATA 属性解析数据运行列表，返回 $MFT 数据逻辑大小
    static long ParseMftRunList(byte[] rec, int recordSize, ref List<Run> runs)
    {
        int attrOff = BitConverter.ToUInt16(rec, 0x14);
        while (attrOff + 4 <= recordSize)
        {
            uint atype = BitConverter.ToUInt32(rec, attrOff);
            if (atype == ATTR_END) break;
            int alen = (int)BitConverter.ToUInt32(rec, attrOff + 4);
            if (alen <= 0 || attrOff + alen > recordSize) break;

            if (atype == ATTR_DATA && rec[attrOff + 8] != 0 && rec[attrOff + 9] == 0)
            {
                long realSize = BitConverter.ToInt64(rec, attrOff + 0x30);
                int runOff = attrOff + BitConverter.ToUInt16(rec, attrOff + 0x20);
                int p = runOff;
                long lcn = 0;
                while (p < attrOff + alen)
                {
                    byte hdr = rec[p++];
                    if (hdr == 0) break;
                    int lenBytes = hdr & 0x0F;
                    int offBytes = (hdr >> 4) & 0x0F;
                    if (lenBytes == 0 || p + lenBytes + offBytes > attrOff + alen) break;

                    long length = 0;
                    for (int i = 0; i < lenBytes; i++) length |= (long)rec[p + i] << (8 * i);
                    p += lenBytes;

                    long offset = 0;
                    for (int i = 0; i < offBytes; i++) offset |= (long)rec[p + i] << (8 * i);
                    if (offBytes > 0 && (rec[p + offBytes - 1] & 0x80) != 0) offset |= -1L << (8 * offBytes);
                    p += offBytes;

                    lcn += offset;
                    runs.Add(new Run { Lcn = lcn, Clusters = length });
                }
                return realSize;
            }
            attrOff += alen;
        }
        throw new IOException("未能从记录 0 解析出 $MFT 的 $DATA 数据运行");
    }

    static void ReadMftData(Volume vol, List<Run> runs, long clusterSize, long dataOffset, byte[] buf, int count)
    {
        int written = 0;
        long cumulative = 0;
        int ri = 0;
        long tmpUsed = 0;

        while (ri < runs.Count && cumulative + runs[ri].Clusters * clusterSize <= dataOffset)
        {
            cumulative += runs[ri].Clusters * clusterSize;
            ri++;
        }
        tmpUsed = cumulative;

        var seg = new byte[Math.Min(count, 8 * 1024 * 1024)];
        while (written < count && ri < runs.Count)
        {
            long runStart = tmpUsed;
            long runBytes = runs[ri].Clusters * clusterSize;
            long inRunOff = dataOffset + written - runStart;
            if (inRunOff < 0) inRunOff = 0;
            int avail = (int)Math.Min(Math.Min(runBytes - inRunOff, count - written), seg.Length);
            if (avail <= 0) { ri++; tmpUsed = runStart + runBytes; continue; }
            vol.ReadAt(runs[ri].Lcn * clusterSize + inRunOff, seg, avail);
            Array.Copy(seg, 0, buf, written, avail);
            written += avail;
            if (written >= count) break;
            tmpUsed = runStart + runBytes;
            ri++;
        }
    }
}
