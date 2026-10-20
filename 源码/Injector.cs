using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PclDownloadFix
{
    /// <summary>
    /// 启动 PCL 时以「挂起 → 内存打补丁 → 恢复运行」的方式补回下载页的版本列表加载，
    /// 不修改磁盘上的 PCL 文件，因此 PCL 更新后依然有效。
    /// </summary>
    internal static class Injector
    {
        private const string TargetType = "PCL.PageDownloadInstall";
        private const string TargetMethod = "LoaderInit";
        private const string LoaderFieldType = "PCL.ModDownload";
        private const string LoaderFieldName = "DlClientListLoader";
        private const string StubName = "关闭下载";

        internal static string Launch(string pclPath)
        {
            var bytes = File.ReadAllBytes(pclPath);
            var patch = BuildPatch(bytes);

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            var pi = new PROCESS_INFORMATION();
            var cmd = new StringBuilder("\"" + pclPath + "\"");
            string dir = Path.GetDirectoryName(pclPath);

            if (!CreateProcess(pclPath, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_SUSPENDED,
                    IntPtr.Zero, dir, ref si, out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动 PCL");

            try
            {
                if (patch == null)
                {
                    // 本来就是完好的 PCL（或已被官方修复），直接启动即可
                    if (ResumeThread(pi.hThread) == unchecked((uint)-1))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复 PCL 主线程");
                    return "PCL（无需修复，已直接启动，PID " + pi.dwProcessId + "）";
                }

                IntPtr baseAddr = GetImageBase(pi.hProcess, pi.dwProcessId);
                if (baseAddr == IntPtr.Zero) throw new InvalidOperationException("无法获取 PCL 模块基址。");

                IntPtr target = new IntPtr(baseAddr.ToInt64() + patch.MethodRva);

                uint oldProtect;
                if (!VirtualProtectEx(pi.hProcess, target, patch.Bytes.Length, PAGE_EXECUTE_READWRITE, out oldProtect))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法修改 PCL 内存保护属性（可能被安全软件拦截）");

                IntPtr written;
                bool ok = WriteProcessMemory(pi.hProcess, target, patch.Bytes, (IntPtr)patch.Bytes.Length, out written);
                uint tmp;
                VirtualProtectEx(pi.hProcess, target, patch.Bytes.Length, oldProtect, out tmp);
                if (!ok || written.ToInt64() != patch.Bytes.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "写入补丁失败（可能被安全软件拦截）");

                if (ResumeThread(pi.hThread) == unchecked((uint)-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复 PCL 主线程");

                return "PCL " + patch.Version + "（已应用下载修复，PID " + pi.dwProcessId + "）";
            }
            catch
            {
                try { TerminateProcess(pi.hProcess, 1); } catch { }
                throw;
            }
            finally
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }

        // ---------------- 补丁计算 ----------------

        private sealed class PatchInfo
        {
            internal int MethodRva;
            internal byte[] Bytes;
            internal string Version;
        }

        private static PatchInfo BuildPatch(byte[] file)
        {
            ModuleDefMD mod;
            using (var ms = new MemoryStream(file))
                mod = ModuleDefMD.Load(ms);

            using (mod)
            {
                var type = mod.Find(TargetType, false);
                var method = type == null ? null : type.Methods.FirstOrDefault(m => m.Name == TargetMethod);
                if (method == null || method.Body == null)
                    throw new InvalidOperationException("这个 PCL 不是 2.99，或结构已变化：找不到 " + TargetType + "." + TargetMethod + "。");

                var ins = method.Body.Instructions;
                int start = -1, end = -1;
                for (int i = 0; i < ins.Count; i++)
                {
                    if (start < 0 && ins[i].OpCode.Code == Code.Ldstr && (ins[i].Operand as string) == StubName) start = i;
                    if (start >= 0 && ins[i].OpCode.Code == Code.Newobj && (ins[i].Operand as IMethod)?.DeclaringType?.Name.String.StartsWith("LoaderTask") == true)
                    { end = i; break; }
                }

                var fieldType = mod.Find(LoaderFieldType, false);
                var field = fieldType == null ? null : fieldType.Fields.FirstOrDefault(f => f.Name == LoaderFieldName);
                if (field == null) throw new InvalidOperationException("找不到 " + LoaderFieldType + "." + LoaderFieldName + "，PCL 版本不匹配。");

                if (start < 0 || end < start)
                    return null; // 没有可修的地方：说明这一版 PCL 本来就带下载功能

                // 原方法体
                int rva = (int)(uint)method.RVA;
                int fileOff = RvaToOffset(file, rva);
                if (fileOff < 0) throw new InvalidOperationException("PE 结构解析失败。");

                int firstByte = file[fileOff];
                bool fat = (firstByte & 3) == 3;
                int flags = fat ? BitConverter.ToUInt16(file, fileOff) : 0;
                int headerSize = fat ? (((flags >> 12) & 0xF) * 4) : 1;   // 头大小在高 4 位，必须以 ushort 计算
                int codeSize = fat ? BitConverter.ToInt32(file, fileOff + 4) : (firstByte >> 2);
                int maxStack = fat ? BitConverter.ToInt16(file, fileOff + 2) : 8;
                int localTok = fat ? BitConverter.ToInt32(file, fileOff + 8) : 0;

                int ilStart = fileOff + headerSize;
                var oldIl = new byte[codeSize];
                Buffer.BlockCopy(file, ilStart, oldIl, 0, codeSize);

                // 占位加载器创建之后的原始指令（ldarg.0 / ldftn / newobj Action / call PageLoaderInit / ret…）
                int newobjLen = 5;
                int tailOffset = (int)ins[end].Offset + newobjLen;
                if (tailOffset >= codeSize || oldIl[tailOffset] != 0x02)
                    throw new InvalidOperationException("方法体解析异常（指令偏移对不上）。");

                var newIl = new List<byte>(codeSize);
                newIl.Add(0x7E); // ldsfld
                newIl.AddRange(BitConverter.GetBytes(field.MDToken.Raw));
                for (int i = tailOffset; i < codeSize; i++) newIl.Add(oldIl[i]);

                // 自检：ldsfld <field> … ret
                if (newIl.Count < 6 || newIl[newIl.Count - 1] != 0x2A ||
                    newIl[5] != 0x02)   // ldsfld 之后应是 ldarg.0（PageLoaderInit 的第 6 个参数）
                    throw new InvalidOperationException("补丁自检失败：指令序列不符合预期，已放弃修改。");

                var body = new List<byte>(headerSize + newIl.Count);
                if (fat)
                {
                    var hdr = new byte[12];
                    BitConverter.GetBytes((ushort)(flags | 3)).CopyTo(hdr, 0);
                    BitConverter.GetBytes((ushort)maxStack).CopyTo(hdr, 2);
                    BitConverter.GetBytes(newIl.Count).CopyTo(hdr, 4);
                    BitConverter.GetBytes(localTok).CopyTo(hdr, 8);
                    body.AddRange(hdr);
                }
                else
                {
                    body.Add((byte)(2 | (newIl.Count << 2)));
                }
                body.AddRange(newIl);

                int capacity = headerSize + codeSize; // 原地替换，不得超出原方法体空间
                if (body.Count > capacity)
                    throw new InvalidOperationException("补丁长度超出方法体空间，无法原地替换。");

                var result = new byte[capacity];
                for (int i = 0; i < result.Length; i++) result[i] = 0x00;
                body.CopyTo(result, 0);

                return new PatchInfo
                {
                    MethodRva = rva,
                    Bytes = result,
                    Version = mod.Assembly.Version.ToString()
                };
            }
        }

        // ---------------- PE 工具 ----------------

        internal static int RvaToOffset(byte[] pe, int rva)
        {
            int peOff = BitConverter.ToInt32(pe, 0x3C);
            if (peOff <= 0 || peOff + 0x18 > pe.Length) return -1;
            int numSections = BitConverter.ToUInt16(pe, peOff + 6);
            int optSize = BitConverter.ToUInt16(pe, peOff + 20);
            int optOff = peOff + 24;
            int secOff = optOff + optSize;
            for (int i = 0; i < numSections; i++)
            {
                int s = secOff + i * 40;
                if (s + 40 > pe.Length) return -1;
                int va = BitConverter.ToInt32(pe, s + 12);
                int rawSize = BitConverter.ToInt32(pe, s + 16);
                int rawPtr = BitConverter.ToInt32(pe, s + 20);
                if (rva >= va && rva < va + Math.Max(rawSize, 1)) return rawPtr + (rva - va);
            }
            return -1;
        }

        private static IntPtr GetImageBase(IntPtr hProcess, int pid)
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            IntPtr ret;
            int status = NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf(pbi), out ret);
            if (status != 0) return IntPtr.Zero;

            IntPtr imageBase = IntPtr.Zero;
            IntPtr read;
            if (!ReadProcessMemory(hProcess, new IntPtr(pbi.PebBaseAddress.ToInt64() + 0x10), ref imageBase,
                    (IntPtr)IntPtr.Size, out read))
                return IntPtr.Zero;
            return imageBase;
        }

        // ---------------- P/Invoke ----------------

        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr Reserved3;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string lpApplicationName, StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr h, uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr addr, int size, uint newProtect, out uint oldProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr addr, byte[] buffer, IntPtr size, out IntPtr written);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr addr, ref IntPtr buffer, IntPtr size, out IntPtr read);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr hProcess, int infoClass,
            ref PROCESS_BASIC_INFORMATION pbi, int size, out IntPtr returnLength);
    }
}

