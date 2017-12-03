using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace HttpProxy
{
    class Program
    {
        private static ILoggerFactory loggerFactory;
        private static ILogger<Program> logger;

        static void Main(string[] args)
        {
            loggerFactory = new LoggerFactory();
            loggerFactory.AddNLog();

            logger = loggerFactory.CreateLogger<Program>();

            var listener = TcpListener.Create(8080);
            listener.Start();
            logger.LogInformation("Listening on 8080");

            StartAcceptLoopAsync(listener, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static async Task StartAcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                using (logger.BeginScope(client.Client.RemoteEndPoint))
                {
                    logger.LogInformation("ACCEPT");
                    await Task.Factory.StartNew(
                        () => StartForwardLoopAsync(client, cancellationToken),
                        cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
            }
        }

        private static async Task StartForwardLoopAsync(TcpClient client, CancellationToken cancellationToken)
        {
            // Parse the content & forward it
            // https://tools.ietf.org/html/rfc7230#section-3
            // https://tools.ietf.org/html/rfc7230#section-5
            // Read the start-line
            // Parse the headers https://msdn.microsoft.com/en-us/library/system.net.http.headers.namevalueheadervalue.parse(v=vs.118).aspx
            // Read the content according to the headers
            // Forward
            // Loop

            // TODO: Authentication
            // https://tools.ietf.org/html/rfc7235
            // https://referencesource.microsoft.com/#System/net/System/Net/HttpListener.cs,ad37edcb56ce8abd,references

            var clientStream = client.GetStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var requestLine = clientStream.ReadLineASCII();
                logger.LogDebug("request-line: {0}", requestLine);
                string method, target, version;
                (method, target, version) = requestLine.Split(new[] { ' ' }, 3);
                var url = new Uri(target);

                using (logger.BeginScope(url))
                {
                    if (method.Equals("CONNECT", StringComparison.InvariantCultureIgnoreCase))
                    {
                        logger.LogWarning("Drop CONNECT method proxy request");
                        await clientStream.WriteLineAsync($"{version} 501 Not Implemented", Encoding.ASCII);
                        await clientStream.WriteLineAsync();
                        continue;
                    }

                    try
                    {
                        logger.LogInformation("BEGIN");

                        // TODO: Connection reuse
                        var remote = new TcpClient();
                        await remote.ConnectAsync(url.Host, url.Port);

                        var remoteStream = remote.GetStream();

                        using (logger.BeginScope("->"))
                        {
                            await ForwardUpstreamingAsync(clientStream, remoteStream, requestLine);
                        }

                        logger.LogInformation("FINISH -> forwarding");

                        using (logger.BeginScope("<-"))
                        {
                            await ForwardDownstreamingAsync(clientStream, remoteStream);
                        }

                        logger.LogInformation("END");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(new EventId(), ex, "Error: {0}", ex);
                    }
                }
            }
        }

        private static async Task ForwardUpstreamingAsync(NetworkStream clientStream, NetworkStream remoteStream, string requestLine)
        {
            logger.LogDebug(requestLine);
            await remoteStream.WriteLineAsync(requestLine, Encoding.ASCII);
            //await remoteStream.FlushAsync();
            logger.LogTrace("Upstreaming REQUEST-LINE finished");

            var headers = new SortedDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            while (true)
            {
                var line = clientStream.ReadLineASCII();
                logger.LogDebug(line);
                await remoteStream.WriteLineAsync(line);
                if (string.IsNullOrEmpty(line)) break;

                var group = line.Split(new[] { ':' }, 2);
                headers.TryAdd(group[0], group[1].Trim());
            }

            logger.LogTrace("Upstreaming HEADERS finished");
            //await remoteStream.FlushAsync();

            if (headers.TryGetValue("Content-Length", out var lengthStr))
            {
                var length = int.Parse(lengthStr);
                logger.LogInformation("Transfering client request content to remote: {0} bytes", length);

                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, length));
                try
                {
                    for (var readLength = 0; readLength < length;)
                    {
                        var count = await clientStream.ReadAsync(buffer, 0, Math.Min(buffer.Length, length - readLength));
                        if (count == 0) break;

                        await remoteStream.WriteAsync(buffer, 0, count);
                        logger.LogInformation("{0} bytes transfered, {1} bytes remained", count, length - readLength - count);
                        readLength += count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            logger.LogTrace("Upstreaming MESSAGE-BODY finished");
            //await remoteStream.FlushAsync();
        }

        private static async Task ForwardDownstreamingAsync(NetworkStream clientStream, NetworkStream remoteStream)
        {
            //while (true)
            //{
            //    int ch = remoteStream.ReadByte();
            //    if (ch == -1) return;
            //    Console.Write((char)ch);
            //}
            var statusLine = remoteStream.ReadLineASCII();
            logger.LogDebug("status-line: {0}", statusLine);
            await clientStream.WriteLineAsync(statusLine);
            //await clientStream.FlushAsync();
            logger.LogTrace("Downstreaming STATUS-LINE finished");

            var headers = new SortedDictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            while (true)
            {
                var line = remoteStream.ReadLineASCII();
                logger.LogDebug(line);
                await clientStream.WriteLineAsync(line);
                if (string.IsNullOrEmpty(line)) break;

                var group = line.Split(new[] { ':' }, 2);
                headers.TryAdd(group[0], group[1].Trim());
            }

            await clientStream.FlushAsync();
            logger.LogTrace("Downstreaming HEADERS finished");

            if (headers.TryGetValue("Content-Length", out var lengthStr))
            {
                var length = int.Parse(lengthStr);
                logger.LogInformation("Transfering remote response content to client: {0} bytes", length);

                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(4096, length));
                try
                {
                    for (var readLength = 0; readLength < length;)
                    {
                        var count = await remoteStream.ReadAsync(buffer, 0, Math.Min(buffer.Length, length - readLength));
                        if (count == 0) break;

                        await clientStream.WriteAsync(buffer, 0, count);
                        logger.LogInformation("{0} bytes transfered, {1} bytes remained", count, length - readLength - count);
                        readLength += count;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                var transferEncoding = headers["Transfer-Encoding"];
                if (transferEncoding == "chunked")
                {
                    logger.LogInformation("Transfer chunked data");
                    var buffer = ArrayPool<byte>.Shared.Rent(4096);
                    try
                    {
                        while (true)
                        {
                            var chunkMetadata = remoteStream.ReadLineASCII();
                            logger.LogDebug("chunk-metadata: {0}", chunkMetadata);
                            await clientStream.WriteLineAsync(chunkMetadata);
                            await clientStream.FlushAsync();

                            var chunkSizeEndPos = chunkMetadata.IndexOf(';');
                            int chunkSize = int.Parse(
                                (chunkSizeEndPos == -1)
                                ? chunkMetadata.Substring(0)
                                : chunkMetadata.Substring(0, chunkSizeEndPos),
                                NumberStyles.HexNumber);
                            logger.LogDebug("chunk-size: 0x{0:X}, {1}", chunkSize, chunkSize);

                            if (chunkSize == 0) break;
                            for (var readLength = 0; readLength < chunkSize;)
                            {
                                var count = await remoteStream.ReadAsync(buffer, 0, Math.Min(buffer.Length, chunkSize - readLength));
                                if (count == 0) break;

                                await clientStream.WriteAsync(buffer, 0, count);
                                logger.LogInformation("{0} bytes transfered, {1} bytes remained", count, chunkSize - readLength - count);
                                readLength += count;
                            }
                            // Eat line ending after chunk-data
                            remoteStream.ReadLineASCII();
                            await clientStream.WriteLineAsync();
                        }
                        logger.LogInformation("All chunks transfered.");

                        while (true)
                        {
                            var line = remoteStream.ReadLineASCII();
                            await clientStream.WriteLineAsync(line);
                            if (string.IsNullOrEmpty(line)) break;
                        }
                        logger.LogInformation("Tailer transfered.");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                else
                {
                    logger.LogWarning("Not implemented for {0}", transferEncoding);
                }
            }

            logger.LogTrace("Downstreaming MESSAGE-BODY finished");
            await clientStream.FlushAsync();
        }
    }
}
