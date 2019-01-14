using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// This example is a little odd because each instance runs its own server pipe
// and creates client pipe connections on demand. In a real app, the client would
// probably connect to the server and remain connected, and both clients and servers
// have read/write capability. However, servers are limited to a single client so
// the server would need to manage multiple server instances if many clients were
// meant to be connected simultaneously.

namespace NamedPipesComplete
{
    class Program
    {
        const string PIPENAME1 = "pipe_1";
        const string PIPENAME2 = "pipe_2";

        static string ThisPipeName = string.Empty;
        static string OtherPipeName = string.Empty;

        static NamedPipeServerStream server = null;
        static NamedPipeClientStream client = null;

        // very short timeout is ok for local machine
        static int TimeoutMS = 10; 

        // declared here so that tasks can share the token and cancel from inside the tasks
        static CancellationTokenSource tokenSource;
        static CancellationToken token;

        // controls whether we start a new line for local and remote output
        static bool? LastOutputWasLocal = null;

        static UnicodeEncoding unicode = new UnicodeEncoding();

        static async Task Main(string[] args) // set project to C# 7.1 or newer
        {
            bool running = true;
            while(running)
            {
                if(!await Reset())
                {
                    Console.WriteLine("Startup failed. Press any key to exit.");
                    Console.ReadKey(true);
                    running = false;
                }
                else
                {
                    Console.WriteLine($"Server name for this instance: {ThisPipeName}");
                    Console.WriteLine("Type something and press Enter, or press ESC to quit.");
                    Console.WriteLine($"{new string('-', 60)}\n");

                    try
                    {
                        Task.WaitAll(PipeServer(), KeyboardInput());
                    }
                    catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
                    {
                        // OperationCanceledException and TaskCanceledException are normal, ignore them
                    }
                    finally
                    {
                        tokenSource?.Dispose();
                        tokenSource = null;
                    }

                    Console.WriteLine($"\n\n{new string('-', 60)}");
                    Console.WriteLine($"Pipe closed.\n\nPress ESC again to exit, or another key to reset.");
                    running = (Console.ReadKey(true).Key != ConsoleKey.Escape);
                }
            }
        }

        static async Task<bool> Reset()
        {
            Console.Clear();
            Console.WriteLine("Searching for another instance...");

            tokenSource = new CancellationTokenSource();
            token = tokenSource.Token;

            ThisPipeName = string.Empty;
            OtherPipeName = PIPENAME1;
            if (await WriteToServer(null))
            {
                // success means another instance is already running as PIPENAME1
                ThisPipeName = PIPENAME2;
                await WriteToServer($"Hello from {ThisPipeName}!");
            }
            else
            {
                // we are the first instance, the other will name itself PIPENAME2
                ThisPipeName = PIPENAME1;
                OtherPipeName = PIPENAME2;
            }

            return !string.IsNullOrEmpty(ThisPipeName);
        }

        static async Task PipeServer()
        {
            try
            {
                server = new NamedPipeServerStream(ThisPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                do
                {
                    await server.WaitForConnectionAsync(token);
                    token.ThrowIfCancellationRequested();
                    int len = server.ReadByte() * 256 + server.ReadByte();
                    if (len > 0)
                    {
                        byte[] buffer = new byte[len];
                        await server.ReadAsync(buffer, 0, len, token);
                        string data = unicode.GetString(buffer);
                        if (LastOutputWasLocal ?? false) Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write(data);
                        Console.ResetColor();
                        LastOutputWasLocal = false;
                    }
                    server.Disconnect();
                } while (!token.IsCancellationRequested);
            }
            finally
            {
                server?.Dispose();
            }
        }

        static async Task KeyboardInput()
        {
            while(!token.IsCancellationRequested)
            {
                // request for async readkey; for now we have to loop watching KeyAvailable
                // https://github.com/dotnet/corefx/issues/25036
                if(Console.KeyAvailable)
                {
                    var keyinfo = Console.ReadKey(true);
                    switch (keyinfo.Key)
                    {
                        case ConsoleKey.Escape:
                            tokenSource.Cancel();
                            break;

                        case ConsoleKey.Enter:
                            Console.WriteLine();
                            await WriteToServer("\n");
                            break;

                        default:
                            if (!LastOutputWasLocal ?? false) Console.WriteLine();
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.Write(keyinfo.KeyChar);
                            Console.ResetColor();
                            LastOutputWasLocal = true;
                            await WriteToServer(keyinfo.KeyChar.ToString());
                            break;
                    }
                }
            }
        }

        static async Task<bool> WriteToServer(string message)
        {
            NamedPipeClientStream client = null;
            bool success = false;

            try
            {
                client = new NamedPipeClientStream(".", OtherPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(TimeoutMS, token);
                success = client.IsConnected;
                token.ThrowIfCancellationRequested();
                if(client.IsConnected && !string.IsNullOrEmpty(message))
                {
                    byte[] buffer = unicode.GetBytes(message);
                    int len = buffer.Length;
                    if (len > UInt16.MaxValue) len = (int)UInt16.MaxValue;
                    client.WriteByte((byte)(len / 256));
                    client.WriteByte((byte)(len & 255));
                    await client.WriteAsync(buffer, 0, len, token);
                    client.WaitForPipeDrain(); // stream Flush not supported https://docs.microsoft.com/en-us/dotnet/api/system.io.pipes.pipestream.flush?view=netcore-2.2
                }
            }
            catch (TimeoutException)
            {
                success = false;
            }
            catch (IOException)
            {
                success = false;
            }
            finally
            {
                client?.Dispose();
                client = null;
            }

            return success;
        }
    }
}
