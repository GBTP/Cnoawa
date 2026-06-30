namespace Cnoawa;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Cnoawa - Community Node of Anoawa");
            Console.WriteLine();
            Console.WriteLine("用法: cnoawa <port> <apiUrl> <publicAddress> [name] [message] [registrationKey]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  port              监听端口");
            Console.WriteLine("  apiUrl            主 API 地址 (如 https://api.anoawa.com)");
            Console.WriteLine("  publicAddress     本节点公网地址 (IP 或域名)");
            Console.WriteLine("  name              节点名称 (可选)");
            Console.WriteLine("  message           节点寄语 (可选)");
            Console.WriteLine("  registrationKey   注册密钥 (可选，也可通过环境变量 NODE_REGISTRATION_KEY 设置)");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  cnoawa 7776 https://api.anoawa.com mynode.ddns.net \"社区节点\" \"欢迎来玩\" mykey123");
            return;
        }

        var port = ushort.Parse(args[0]);
        var apiUrl = args[1];
        var publicAddress = args[2];
        var name = args.Length > 3 ? args[3] : $"节点-{Environment.MachineName}";
        var message = args.Length > 4 ? args[4] : "欢迎";
        var registrationKey = args.Length > 5 ? args[5] : Environment.GetEnvironmentVariable("NODE_REGISTRATION_KEY") ?? "";

        if (string.IsNullOrEmpty(registrationKey))
        {
            Console.WriteLine("[Cnoawa] 警告: 未提供注册密钥，注册将失败");
        }

        var node = new GameNode(port);
        node.ApiUrl = apiUrl.TrimEnd('/');

        var registration = new ApiRegistration(apiUrl, publicAddress, port, name, message, registrationKey, node);
        node.OnRoomStateChanged = () => { registration.TriggerHeartbeat(); return Task.CompletedTask; };

        var cts = new CancellationTokenSource();
        registration.OnForceUpdate = () =>
        {
            Console.WriteLine("[Cnoawa] 收到更新指令，正在关闭...");
            cts.Cancel();
        };
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[Cnoawa] 正在关闭...");
            cts.Cancel();
        };

        var nodeTask = node.RunAsync(cts.Token);
        await node.ListeningReady.Task;

        Console.WriteLine($"[Cnoawa] 正在向 {apiUrl} 注册...");
        var registered = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            registered = await registration.RegisterAsync();
            if (registered) break;
            if (attempt < 3)
            {
                Console.WriteLine($"[Cnoawa] 注册失败，5秒后重试 ({attempt}/3)...");
                await Task.Delay(5000);
            }
        }

        if (!registered)
        {
            Console.WriteLine("[Cnoawa] 注册失败。没有 JWT 公钥无法验证玩家身份，节点无法运行。");
            Console.WriteLine("[Cnoawa] 请检查：1) 主 API 是否在线  2) 注册密钥是否正确  3) 本节点是否可从外网访问");
            cts.Cancel();
            return;
        }

        Task? heartbeatTask = Task.Run(() => registration.HeartbeatLoop(cts.Token));

        try
        {
            await nodeTask;
        }
        finally
        {
            await registration.UnregisterAsync();
            if (heartbeatTask != null)
                await heartbeatTask;
        }
    }
}
