#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Airbyte.Cdk.IntegrationTests
{
    public sealed class RunningConnector : IDisposable
    {
        private readonly Process _process;
        private readonly object _gate = new();
        private readonly List<JsonElement> _messages = new();
        private readonly StringBuilder _output = new();
        private readonly Task _stdout;
        private readonly Task _stderr;
        private bool _timedOut;
        private bool _killed;
        private int? _exitCode;

        internal RunningConnector(Process process)
        {
            _process = process;
            _stdout = Task.Run(ReadStandardOutputAsync);
            _stderr = Task.Run(ReadStandardErrorAsync);
        }

        public int ExitCode
        {
            get
            {
                lock (_gate)
                    return _exitCode ?? -1;
            }
        }

        public bool TimedOut
        {
            get
            {
                lock (_gate)
                    return _timedOut;
            }
        }

        public IReadOnlyList<JsonElement> Messages
        {
            get
            {
                lock (_gate)
                    return _messages.ToList();
            }
        }

        public string Output
        {
            get
            {
                lock (_gate)
                    return _output.ToString().TrimEnd();
            }
        }

        public async Task<int> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!_process.HasExited)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancellation.CancelAfter(timeout);
                try
                {
                    await _process.WaitForExitAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                        _timedOut = true;
                    Kill();
                }
            }

            try
            {
                using var forced = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _process.WaitForExitAsync(forced.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            await DrainAsync();

            var exitCode = -1;
            try
            {
                exitCode = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
            }

            lock (_gate)
            {
                _exitCode ??= exitCode;
                return _exitCode.Value;
            }
        }

        public void Kill()
        {
            lock (_gate)
            {
                if (_killed)
                    return;

                _killed = true;
            }

            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            Kill();
            try
            {
                Task.WaitAll(new[] { _stdout, _stderr }, TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }

            _process.Dispose();
        }

        private async Task DrainAsync()
        {
            try
            {
                await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (Exception)
            {
            }
        }

        private async Task ReadStandardOutputAsync()
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync();
                if (line == null)
                    return;

                if (MessageParser.TryParse(line, out var message))
                {
                    lock (_gate)
                        _messages.Add(message);
                }
                else
                {
                    lock (_gate)
                        _output.AppendLine(line);
                }
            }
        }

        private async Task ReadStandardErrorAsync()
        {
            while (true)
            {
                var line = await _process.StandardError.ReadLineAsync();
                if (line == null)
                    return;

                lock (_gate)
                    _output.AppendLine(line);
            }
        }
    }
}
