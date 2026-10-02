using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SPerformanceT.Affinity
{
    /// <summary>
    /// The Windows calls, straight to kernel32. System.Diagnostics.Process is avoided on purpose:
    /// what Unity's Mono implements of it on Windows is not something to bet a feature on.
    /// </summary>
    internal static class Native
    {
        private const int ErrorInsufficientBuffer = 122;
        private const uint ProcessSetInformation = 0x0200;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint Th32csSnapProcess = 0x00000002;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        /// <summary>
        /// Every logical processor with its core and efficiency class. Offsets are those of
        /// SYSTEM_CPU_SET_INFORMATION: Type @4, Group @12 (WORD), LogicalProcessorIndex @14,
        /// CoreIndex @15, EfficiencyClass @18.
        /// </summary>
        public static List<LogicalCpu> ReadTopology()
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
                throw new Win32Exception(error, "GetSystemCpuSetInformation (size query)");

            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation");

                var cpus = new List<LogicalCpu>();
                int offset = 0;
                while (offset < length)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0)
                        break;
                    int type = Marshal.ReadInt32(buffer, offset + 4);
                    if (type == 0) // CpuSetInformation
                    {
                        cpus.Add(new LogicalCpu(
                            group: (ushort)Marshal.ReadInt16(buffer, offset + 12),
                            index: Marshal.ReadByte(buffer, offset + 14),
                            core: Marshal.ReadByte(buffer, offset + 15),
                            efficiencyClass: Marshal.ReadByte(buffer, offset + 18)));
                    }
                    offset += size;
                }
                return cpus;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public static IntPtr CurrentProcess => GetCurrentProcess();

        /// <summary>Opens a process by exe name ("SPT.Server.exe"), or returns IntPtr.Zero if none is running.</summary>
        public static IntPtr OpenByExeName(string exeName)
        {
            uint pid = FindPid(exeName);
            if (pid == 0)
                return IntPtr.Zero;
            IntPtr handle = OpenProcess(ProcessSetInformation | ProcessQueryLimitedInformation, false, pid);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess " + exeName + " (pid " + pid + ")");
            return handle;
        }

        public static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }

        public static void GetMasks(IntPtr process, out ulong processMask, out ulong systemMask)
        {
            if (!GetProcessAffinityMask(process, out UIntPtr p, out UIntPtr s))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessAffinityMask");
            processMask = p.ToUInt64();
            systemMask = s.ToUInt64();
        }

        public static void SetMask(IntPtr process, ulong mask)
        {
            if (!SetProcessAffinityMask(process, new UIntPtr(mask)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetProcessAffinityMask");
        }

        private static uint FindPid(string exeName)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == InvalidHandle)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot");
            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                {
                    if (string.Equals(entry.ExeFile, exeName, StringComparison.OrdinalIgnoreCase))
                        return entry.ProcessId;
                }
                return 0;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(
            IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);
    }
}
