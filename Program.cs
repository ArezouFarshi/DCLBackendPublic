using System;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.Web3;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;

[Event("PaymentMilestoneReached")]
public class PaymentMilestoneReachedEventDTO : IEventDTO
{
    [Parameter("uint8", "paymentPercentage", 1, false)]
    public byte PaymentPercentage { get; set; }

    [Parameter("string", "windowName", 2, false)]
    public string WindowName { get; set; }
}

class Program
{
    static int latestMilestone = 0;
    static ConcurrentBag<string> visibleWindows = new ConcurrentBag<string>();
    static ConcurrentBag<WebSocket> clients = new ConcurrentBag<WebSocket>();

    static async Task StartWebSocketServer()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();

        Console.WriteLine($"✅ WebSocket server listening on http://0.0.0.0:{port}/");

        while (true)
        {
            var context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                var socket = wsContext.WebSocket;
                Console.WriteLine("🌐 Unity client connected.");

                var snapshot = new
                {
                    type = "snapshot",
                    currentMilestone = latestMilestone,
                    windowsVisible = visibleWindows.ToArray()
                };
                var snapMsg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot));
                await socket.SendAsync(new ArraySegment<byte>(snapMsg),
                                      WebSocketMessageType.Text, true, CancellationToken.None);

                clients.Add(socket);
            }
            else if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/api/test")
            {
                var response = JsonSerializer.Serialize(new { status = "success", timestamp = DateTime.UtcNow });
                var message = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = message.Length;
                await context.Response.OutputStream.WriteAsync(message, 0, message.Length);
                context.Response.OutputStream.Close();
            }
            else if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/api/visibility")
            {
                var visibility = new Dictionary<string, bool>
                {
                    { "1stStoryWindows", visibleWindows.Contains("1stStoryWindows") },
                    { "2ndStoryWindows", visibleWindows.Contains("2ndStoryWindows") },
                    { "3rdStoryWindows", visibleWindows.Contains("3rdStoryWindows") },
                    { "4thStoryWindows", visibleWindows.Contains("4thStoryWindows") }
                };

                var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(visibility));
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = message.Length;
                await context.Response.OutputStream.WriteAsync(message, 0, message.Length);
                context.Response.OutputStream.Close();
            }
            else
            {
                var message = Encoding.UTF8.GetBytes("👋 MonaBackend is running!");
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = message.Length;
                await context.Response.OutputStream.WriteAsync(message, 0, message.Length);
                context.Response.OutputStream.Close();
            }
        }
    }

    static async Task StartBlockchainListener()
    {
        var web3 = new Web3("https://sepolia.infura.io/v3/51bc36040f314e85bf103ff18c570993");
        var contractAddress = "0x7C3dc63D5Ba4046F57680b24A1362f4052535378";
        var eventHandler = web3.Eth.GetEvent<PaymentMilestoneReachedEventDTO>(contractAddress);

        Console.WriteLine("👂 Listening for PaymentMilestoneReached events...");

        var lastBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

        while (true)
        {
            var currentBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

            if (currentBlock.Value > lastBlock.Value)
            {
                var filter = eventHandler.CreateFilterInput(
                    new BlockParameter(new HexBigInteger(lastBlock.Value + 1)),
                    new BlockParameter(new HexBigInteger(currentBlock.Value))
                );

                var logs = await eventHandler.GetAllChangesAsync(filter);

                foreach (var ev in logs)
                {
                    byte percentage = ev.Event.PaymentPercentage;
                    string windowName = ev.Event.WindowName;

                    Console.WriteLine($"[Blockchain] Milestone: {percentage}% for {windowName}");

                    latestMilestone = Math.Max(latestMilestone, percentage);
                    if (!visibleWindows.Contains(windowName))
                        visibleWindows.Add(windowName);

                    var json = JsonSerializer.Serialize(new
                    {
                        type = "update",
                        paymentPercentage = percentage,
                        windowName = windowName
                    });
                    var message = Encoding.UTF8.GetBytes(json);

                    foreach (var socket in clients)
                    {
                        if (socket.State == WebSocketState.Open)
                        {
                            await socket.SendAsync(new ArraySegment<byte>(message),
                                                   WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                }

                lastBlock = currentBlock;
            }

            await Task.Delay(20000);
        }
    }

    static async Task Main(string[] args)
    {
        await Task.WhenAll(StartWebSocketServer(), StartBlockchainListener());
    }
}
