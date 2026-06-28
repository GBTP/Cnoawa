namespace Cnoawa;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Cnoawa - Community Node of Anoawa");
            Console.WriteLine();
            Console.WriteLine("用法: cnoawa <port> <apiUrl> <publicAddress> [name] [message]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  port           监听端口");
            Console.WriteLine("  apiUrl         主 API 地址 (如 https://api.anoawa.com)");
            Console.WriteLine("  publicAddress  本节点公网地址 (IP 或域名)");
            Console.WriteLine("  name           节点名称 (可选)");
            Console.WriteLine("  message        节点寄语 (可选)");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  cnoawa 7776 https://api.anoawa.com mynode.ddns.net \"社区节点\" \"欢迎来玩\"");
            return;
        }

        var port = ushort.Parse(args[0]);
        var apiUrl = args[1];
        var publicAddress = args[2];
        var name = args.Length > 3 ? args[3] : $"节点-{Environment.MachineName}";
        var message = args.Length > 4 ? args[4] : "欢迎";

        var node = new GameNode(port)
        {
            Name = name,
            Message = message,
            ApiUrl = apiUrl
        };

        var registration = new ApiRegistration(apiUrl, publicAddress, port, name, message, node);

        Console.WriteLine($"[Cnoawa] 正在向 {apiUrl} 注册...");
        var registered = await registration.RegisterAsync();
        if (!registered)
        {
            Console.WriteLine("[Cnoawa] 注册失败，将以离线模式运行（不在主 API 列表中显示）");
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[Cnoawa] 正在关闭...");
            cts.Cancel();
        };

        Task? heartbeatTask = null;
        if (registered)
            heartbeatTask = Task.Run(() => registration.HeartbeatLoop(cts.Token));

        try
        {
            await node.RunAsync(cts.Token);
        }
        finally
        {
            if (registered)
            {
                await registration.UnregisterAsync();
                if (heartbeatTask != null)
                    await heartbeatTask;
            }
        }
    }
}
