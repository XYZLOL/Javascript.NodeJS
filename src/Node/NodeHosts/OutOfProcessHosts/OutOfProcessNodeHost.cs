using Jering.JavascriptUtils.Node.Node.OutOfProcessHosts;
using Jering.JavascriptUtils.Node.NodeHosts;
using Jering.JavascriptUtils.Node.NodeHosts.OutOfProcessHosts;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jering.JavascriptUtils.Node.HostingModels
{
    /// <summary>
    /// <para>The primary responsibilities of this class are launching and maintaining a Node.js process.</para>
    /// <para>
    /// This abstract base class uses the input/output streams of the child process to perform a simple handshake
    /// to determine when the child process is ready to accept invocations. This is agnostic to the mechanism that
    /// derived classes use to actually perform the invocations (e.g., they could use HTTP-RPC, or a binary TCP
    /// protocol, or any other RPC-type mechanism).
    /// </para>
    /// </summary>
    /// <seealso cref="INodeHost" />
    public abstract class OutOfProcessNodeHost : INodeHost
    {
        private const string CONNECTION_ESTABLISHED_MESSAGE = "[Jering.JavascriptUtils.Node:Listening]";

        private readonly INodeProcessFactory _nodeProcessFactory;
        private readonly string _nodeServerScript;
        private readonly IInvocationDataFactory _invocationDataFactory;
        private readonly ILogger _nodeOutputLogger;
        private readonly OutOfProcessNodeHostOptions _options;

        private SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private Process _nodeProcess;
        private bool _disposed;

        /// <summary>
        /// Creates a new instance of <see cref="OutOfProcessNodeHost"/>.
        /// </summary>
        /// <param name="nodeProcessFactory"></param>
        /// <param name="nodeServerScript">The server script to run in the Node.js process.</param>
        /// <param name="invocationDataFactory"></param>
        /// <param name="nodeOutputLogger">The <see cref="ILogger"/> to which the Node.js process's stdout/stderr (and other log information) will be redirected to.</param>
        /// <param name="options"></param>
        protected OutOfProcessNodeHost(INodeProcessFactory nodeProcessFactory,
            string nodeServerScript,
            IInvocationDataFactory invocationDataFactory,
            ILogger nodeOutputLogger,
            OutOfProcessNodeHostOptions options)
        {
            _nodeProcessFactory = nodeProcessFactory;
            _nodeServerScript = nodeServerScript;
            _invocationDataFactory = invocationDataFactory;
            _nodeOutputLogger = nodeOutputLogger ?? throw new ArgumentNullException(nameof(nodeOutputLogger));
            _options = options;
        }

        /// <summary>
        /// Asynchronously invokes code in the Node.js instance.
        /// </summary>
        /// <typeparam name="T">The JSON-serializable data type that the Node.js code will asynchronously return.</typeparam>
        /// <param name="invocationData">Contains the data to be sent to the Node.js process.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the invocation.</param>
        /// <returns>A <see cref="Task{TResult}"/> representing the completion of the RPC call.</returns>
        protected abstract Task<T> InvokeAsync<T>(InvocationData invocationData, CancellationToken cancellationToken);

        /// <summary>
        /// Called when the connection established message from the Node.js process is received. The server script can be used to customize the message to provide
        /// information on the server, such as the port is is listening on.
        /// </summary>
        /// <param name="connectionEstablishedMessage"></param>
        protected abstract void OnConnectionEstablishedMessageReceived(string connectionEstablishedMessage);

        public Task<T> InvokeFromFileAsync<T>(string modulePath, bool cache = true, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            InvocationData invocationData = _invocationDataFactory.
                Create(ModuleSourceType.File,
                    modulePath, cache ? modulePath : null,
                    exportName,
                    args);

            return InvokeCoreAsync<T>(invocationData, cancellationToken);
        }

        public Task<T> InvokeFromStringAsync<T>(string moduleString, string newCacheIdentifier = null, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            InvocationData invocationData = _invocationDataFactory.
                Create(ModuleSourceType.String,
                    moduleString,
                    newCacheIdentifier,
                    exportName,
                    args);

            return InvokeCoreAsync<T>(invocationData, cancellationToken);
        }

        public Task<T> InvokeFromStreamAsync<T>(Stream moduleStream, string newCacheIdentifier = null, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            InvocationData invocationData = _invocationDataFactory.
                Create(ModuleSourceType.Stream,
                    newCacheIdentifier: newCacheIdentifier,
                    exportName: exportName,
                    args: args,
                    moduleStreamSource: moduleStream);

            return InvokeCoreAsync<T>(invocationData, cancellationToken);
        }

        public Task<TryInvokeFromCacheResult<T>> TryInvokeFromCacheAsync<T>(string moduleCacheIdentifier, string exportName = null, object[] args = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            InvocationData invocationData = _invocationDataFactory.
                Create(ModuleSourceType.Cache,
                    moduleCacheIdentifier,
                    exportName: exportName,
                    args: args);

            return InvokeCoreAsync<TryInvokeFromCacheResult<T>>(invocationData, cancellationToken);
        }

        private async Task<T> InvokeCoreAsync<T>(InvocationData invocationData, CancellationToken cancellationToken)
        {
            try
            {
                // If the the Node.js process has terminated for some reason, attempt to create a new process.
                // Apart from the thread creating the process, all other threads will be blocked. If the new process 
                // is created successfully, all threads will be released by the OutputDataReceived delegate in 
                // ConnectToInputOutputStreams.
                if (_nodeProcess?.HasExited != false)
                {
                    await _semaphore.WaitAsync().ConfigureAwait(false);

                    // Double checked lock
                    if (_nodeProcess?.HasExited != false)
                    {
                        _nodeProcess?.Dispose();
                        _nodeProcess = null;
                        Process newNodeProcess = _nodeProcessFactory.Create(_nodeServerScript);
                        ConnectToInputOutputStreams(newNodeProcess);

                        await _semaphore.WaitAsync(_options.InvocationTimeoutMS, cancellationToken).ConfigureAwait(false);

                        // Successfully connected to new node process
                        _nodeProcess = newNodeProcess;
                    }
                }

                // Create CancellationTokenSources only as required. The following try block cannot be combined with the outer try block
                // because if a NodeInvocationException is thrown and no method down the stack catches it, the logic in finally will never run. 
                CancellationTokenSource timeoutCTS = null;
                CancellationTokenSource combinedCTS = null;
                try
                {
                    if (_options.InvocationTimeoutMS > 0)
                    {
                        timeoutCTS = new CancellationTokenSource();
                        timeoutCTS.CancelAfter(_options.InvocationTimeoutMS);

                        if (cancellationToken != CancellationToken.None)
                        {
                            combinedCTS = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCTS.Token);
                            cancellationToken = combinedCTS.Token;
                        }
                        else
                        {
                            cancellationToken = timeoutCTS.Token;
                        }
                    }

                    return await InvokeAsync<T>(invocationData, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    timeoutCTS?.Dispose();
                    combinedCTS?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                if (_nodeProcess == null)
                {
                    // This is very unlikely
                    throw new NodeInvocationException(
                        $"Attempt to connect to Node timed out after {_options.InvocationTimeoutMS}ms.",
                        string.Empty);
                }

                // Developers encounter this fairly often (if their Node code fails without invoking the callback,
                // all that the .NET side knows is that the invocation eventually times out). Previously, this surfaced
                // as a TaskCanceledException, but this led to a lot of issue reports. Now we throw the following
                // descriptive error.
                throw new NodeInvocationException(
                    $"The Node invocation timed out after {_options.InvocationTimeoutMS}ms.",
                    $"You can change the timeout duration by setting the {nameof(OutOfProcessNodeHostOptions.InvocationTimeoutMS)} "
                    + $"property on {nameof(OutOfProcessNodeHostOptions)}.\n\n"
                    + "The first debugging step is to ensure that your Node.js function always invokes the supplied "
                    + "callback (or throws an exception synchronously), even if it encounters an error. Otherwise, "
                    + "the .NET code has no way to know that it is finished or has failed."
                );
            }
        }

        protected virtual void ConnectToInputOutputStreams(Process nodeProcess)
        {
            bool connectionEstablished = false;
            var outputStringBuilder = new StringBuilder();
            var errorStringBuilder = new StringBuilder();

            nodeProcess.OutputDataReceived += (sender, evt) =>
            {
                if (evt.Data == null)
                {
                    return;
                }

                if (evt.Data.StartsWith(CONNECTION_ESTABLISHED_MESSAGE) && !connectionEstablished)
                {
                    OnConnectionEstablishedMessageReceived(evt.Data);
                    connectionEstablished = true;

                    // Release all threads by resetting CurrentCount to 1
                    while (_semaphore.CurrentCount < 1)
                    {
                        _semaphore.Release();

                        // TODO Remove this after testing
                        if (_nodeOutputLogger.IsEnabled(LogLevel.Debug))
                        {
                            _nodeOutputLogger.LogDebug($"Node process creation semaphor count: {_semaphore.CurrentCount}");
                        }
                    }
                }
                else
                {
                    // Process output is received line by line. The last line of a message ends with a \0 (null character),
                    // so we accumulate lines in a StringBuilder till the \0, then log the entire message in one go.
                    if (TryCreateMessage(outputStringBuilder, evt.Data, out string message))
                    {
                        _nodeOutputLogger.LogInformation(message);
                    }
                }
            };

            nodeProcess.ErrorDataReceived += (sender, evt) =>
            {
                if (evt.Data == null)
                {
                    return;
                }

                if (TryCreateMessage(errorStringBuilder, evt.Data, out string message))
                {
                    _nodeOutputLogger.LogError(message);
                }
            };

            nodeProcess.BeginOutputReadLine();
            nodeProcess.BeginErrorReadLine();
        }

        private bool TryCreateMessage(StringBuilder stringBuilder, string newLine, out string message)
        {
            stringBuilder.AppendLine(newLine);

            if (stringBuilder[stringBuilder.Length - 1] != '\0')
            {
                message = null;
                return false;
            }

            stringBuilder.Length--;
            message = stringBuilder.ToString();
            stringBuilder.Length = 0;

            return true;
        }

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        public void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the instance.
        /// </summary>
        /// <param name="disposing">True if the object is disposing or false if it is finalizing.</param>
        protected virtual void DisposeCore()
        {
            if (!_disposed)
            {
                // Make sure the Node process is finished
                // TODO: Is there a more graceful way to end it? Or does this still let it perform any cleanup?
                if (_nodeProcess?.HasExited == false)
                {
                    _nodeProcess.Kill();
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Implements the finalization part of the IDisposable pattern by calling Dispose(false).
        /// </summary>
        ~OutOfProcessNodeHost()
        {
            DisposeCore();
        }
    }
}