﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Interop;
using BuildXL.Native.IO;
using BuildXL.Utilities;
using BuildXL.Utilities.VmCommandProxy;

namespace BuildXL.Processes
{
    /// <summary>
    /// Sandboxed process that will be executed in VM.
    /// </summary>
    public class ExternalVmSandboxedProcess : ExternalSandboxedProcess
    {
        /// <inheritdoc />
        public override int ProcessId => Process?.Id ?? -1;

        /// <summary>
        /// Underlying managed <see cref="Process"/> object.
        /// </summary>
        public Process Process => m_processExecutor?.Process;

        /// <inheritdoc />
        public override string StdOut => m_processExecutor?.StdOutCompleted ?? false ? m_output.ToString() : string.Empty;

        /// <inheritdoc />
        public override string StdErr => m_processExecutor?.StdErrCompleted ?? false ? m_error.ToString() : string.Empty;

        /// <inheritdoc />
        public override int? ExitCode => m_processExecutor.ExitCompleted ? Process?.ExitCode : default;

        private readonly StringBuilder m_output = new StringBuilder();
        private readonly StringBuilder m_error = new StringBuilder();

        private AsyncProcessExecutor m_processExecutor;

        private readonly ExternalToolSandboxedProcessExecutor m_tool;
        private readonly VmInitializer m_vmInitializer;

        /// <summary>
        /// <see cref="ExitCode"/> represents a VM Command Proxy failure.
        /// </summary>
        public bool HasVmInfrastructureError { get; private set; }

        /// <summary>
        /// Creates an instance of <see cref="ExternalVmSandboxedProcess"/>.
        /// </summary>
        public ExternalVmSandboxedProcess(
            SandboxedProcessInfo sandboxedProcessInfo, 
            VmInitializer vmInitializer, 
            ExternalToolSandboxedProcessExecutor tool,
            string externalSandboxedProcessDirectory,
            SandboxedProcessExecutorTestHook sandboxedProcessExecutorTestHook = null)
            : base(sandboxedProcessInfo, Path.Combine(externalSandboxedProcessDirectory, nameof(ExternalVmSandboxedProcess)), sandboxedProcessExecutorTestHook)
        {
            Contract.Requires(vmInitializer != null);
            Contract.Requires(tool != null);

            m_vmInitializer = vmInitializer;
            m_tool = tool;
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            m_processExecutor?.Dispose();
        }

        /// <inheritdoc />
        public override ProcessMemoryCountersSnapshot? GetMemoryCountersSnapshot() => m_processExecutor?.GetMemoryCountersSnapshot();

        /// <inheritdoc />
        public override EmptyWorkingSetResult TryEmptyWorkingSet(bool isSuspend) => EmptyWorkingSetResult.None; // Only SandboxedProcess is supported.

        /// <inheritdoc />
        public override bool TryResumeProcess() => false; // Currently, only SandboxedProcess is supported.

        /// <inheritdoc />
        [SuppressMessage("AsyncUsage", "AsyncFixer02:MissingAsyncOpportunity")]
        public override async Task<SandboxedProcessResult> GetResultAsync()
        {
            Contract.Requires(m_processExecutor != null);

            // (1) Wait for VmCommandProxy.
            await m_processExecutor.WaitForExitAsync();
            await m_processExecutor.WaitForStdOutAndStdErrAsync();

            // (2) Validate result of VmCommandProxy.
            if (m_processExecutor.TimedOut || m_processExecutor.Killed)
            {
                // If timed out/killed, then sandboxed process result may have not been deserialized yet.
                return CreateResultForVmCommandProxyFailure();
            }

            if (Process.ExitCode != 0)
            {
                return CreateResultForVmCommandProxyFailure();
            }

            if (!FileUtilities.FileExistsNoFollow(RunOutputPath))
            {
                m_error.AppendLine($"Could not find VM output file '{RunOutputPath}");
                return CreateResultForVmCommandProxyFailure();
            }

            try
            {
                // (3) Validate the result of sandboxed process executor run by VmCommandProxy.
                RunResult runVmResult = ExceptionUtilities.HandleRecoverableIOException(
                    () => VmSerializer.DeserializeFromFile<RunResult>(RunOutputPath),
                    e => m_error.AppendLine(e.Message));

                if (runVmResult == null)
                {
                    return CreateResultForVmCommandProxyFailure();
                }

                if (runVmResult.ProcessStateInfo.ExitCode != 0)
                {
                    return CreateResultForSandboxExecutorFailure(runVmResult);
                }
            }
            catch (Exception e)
            {
                m_error.AppendLine(e.ToString());
                return CreateResultForVmCommandProxyFailure();
            }

            return DeserializeSandboxedProcessResultFromFile();
        }

        /// <inheritdoc />
        public override Task KillAsync() => KillProcessExecutorAsync(m_processExecutor);

        /// <inheritdoc />
        public override void Start()
        {
            base.Start();
            RunInVm();
        }

        private void RunInVm()
        {
            // (1) Serialize sandboxed prosess info and SandboxedProcessExecutor Test Hooks.
            SerializeSandboxedProcessInputFile(SandboxedProcessInfoFile, SandboxedProcessInfo.Serialize);
            if (SandboxedProcessExecutorTestHook != null)
            {
                SerializeSandboxedProcessInputFile(SandboxedProcessExecutorTestHookFile, SandboxedProcessInfo.Serialize);
            }

            // (2) Create and serialize run request.
            var runRequest = new RunRequest
            {
                AbsolutePath = m_tool.ExecutablePath,
                Arguments = m_tool.CreateArguments(SandboxedProcessInfoFile, SandboxedProcessResultsFile, SandboxedProcessExecutorTestHookFile),
                WorkingDirectory = WorkingDirectory
            };

            VmSerializer.SerializeToFile(RunRequestPath, runRequest);

            // (2) Create a process to execute VmCommandProxy.
            string arguments = $"{VmCommands.Run} /{VmCommands.Params.InputJsonFile}:\"{RunRequestPath}\" /{VmCommands.Params.OutputJsonFile}:\"{RunOutputPath}\"";
            var process = CreateVmCommandProxyProcess(arguments);

            LogExternalExecution($"call (wd: {process.StartInfo.WorkingDirectory}) {m_vmInitializer.VmCommandProxy} {arguments}");

            m_processExecutor = new AsyncProcessExecutor(
                process,
                TimeSpan.FromMilliseconds(-1), // Timeout should only be applied to the process that the external tool executes.
                line => AppendLineIfNotNull(m_output, line),
                line => AppendLineIfNotNull(m_error, line),
                SandboxedProcessInfo.Provenance,
                message => LogExternalExecution(message));

            m_processExecutor.Start();
        }

        private string RunRequestPath => GetVmCommandProxyPath(nameof(VmCommands.Run), "request.json");

        private string RunOutputPath => GetVmCommandProxyPath(nameof(VmCommands.Run), "output.json");

        private string GetVmCommandProxyPath(string command, string fileName) => Path.Combine(WorkingDirectory, $"{command}_{fileName}");

        private Process CreateVmCommandProxyProcess(string arguments)
        {
            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = m_vmInitializer.VmCommandProxy,
                    Arguments = arguments,
                    WorkingDirectory = WorkingDirectory,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },

                EnableRaisingEvents = true
            };
        }

        private SandboxedProcessResult CreateResultForVmCommandProxyFailure()
        {
            string output = m_output.ToString();
            string error = m_error.ToString();
            string hint = Path.GetFileNameWithoutExtension(m_tool.ExecutablePath);

            return CreateResultForFailure(
                exitCode: m_processExecutor.TimedOut ? ExitCodes.Timeout : Process.ExitCode,
                killed: m_processExecutor.Killed,
                timedOut: m_processExecutor.TimedOut,
                output: output,
                error: error,
                hint: hint);
        }

        private SandboxedProcessResult CreateResultForSandboxExecutorFailure(RunResult runVmResult)
        {
            Contract.Requires(runVmResult != null);

            if (runVmResult.ProcessStateInfo.ExitCode == ExitCodes.VmInfrastructureFailure)
            {
                HasVmInfrastructureError = true;
            }

            return CreateResultForFailure(
                exitCode: runVmResult.ProcessStateInfo.ExitCode,
                killed: false,
                timedOut: false,
                output: runVmResult.StdOut ?? string.Empty,
                error: runVmResult.StdErr ?? string.Empty,
                hint: Path.GetFileNameWithoutExtension(m_tool.ExecutablePath));
        }
    }
}
