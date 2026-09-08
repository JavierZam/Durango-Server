using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Durango.Network;
using Durango.Offline;
using Durango.Utils;
using Messages;
using Shared.Display;
using Shared.System;

namespace DurangoTestClient;

/// <summary>Automated verification for pet system: follow AI, touch wheel, mount, feeding, persistence.</summary>
public static class PetCheck
{
    private static int _passed;
    private static int _failed;
    private static PetsInfo? _petsInfo;
    private static AppearPet? _appearPet;
    private static Touched? _touched;
    private static PlayerDisplay? _display;
    private static readonly List<Move> _petMoves = new List<Move>();
    private static FeedingSuccess? _feedingSuccess;
    private static readonly List<string> _infos = new List<string>();

    private static void Pump(Connection connection, int milliseconds)
    {
        for (int i = 0; i < milliseconds / 10; i++)
        {
            connection.Process();
            Thread.Sleep(10);
        }
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        if (ok) { _passed++; Console.WriteLine("  [PASS] " + name + (detail == null ? "" : " - " + detail)); }
        else { _failed++; Console.WriteLine("  [FAIL] " + name + (detail == null ? "" : " - " + detail)); }
    }

    private static Connection Connect(string host, int gamePort, int gatewayPort, ref string id, string claimedName)
    {
        string token = SessionClient.Fetch(host, gatewayPort, id, claimedName);
        if (string.IsNullOrEmpty(token)) return null;
        if (!string.IsNullOrEmpty(SessionClient.LastUserId)) { id = SessionClient.LastUserId; }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(host, gamePort);
        var c = new Connection(socket);

        c.Recv<Welcome>((m, h) => { });
        c.Recv<Clock>((m, h) => { });
        c.Recv<OK>((m, h) => { });
        c.Recv<Info>((m, h) => { if (!string.IsNullOrEmpty(m.Text)) _infos.Add(m.Text); });
        c.Recv<PetsInfo>((m, h) => _petsInfo = m);
        c.Recv<AppearPet>((m, h) => _appearPet = m);
        c.Recv<Touched>((m, h) => _touched = m);
        c.Recv<PlayerDisplay>((m, h) => _display = m);
        c.Recv<FeedingSuccess>((m, h) => _feedingSuccess = m);
        c.Recv<Messages.Pet>((m, h) => { });
        c.Recv<SayInExclusiveChannel>((m, h) => { });
        c.Recv<InventoryUpdated>((m, h) => { });
        c.Recv<Move>((m, h) =>
        {
            if (m.EntityId != null && m.EntityId.StartsWith("pet_"))
            {
                _petMoves.Add(m);
            }
        });

        c.StartReceive();
        c.Send(new GetClock { Time = Times.UnixTimeNow() }); Pump(c, 250);
        c.Send(new Auth { EntityId = id, SessionToken = token, ClientVersion = "5.2.1", DeviceModel = "pet-check" }); Pump(c, 450);
        c.Send(default(Ready)); Pump(c, 1500);
        return c;
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            var u = new UdpClient(port);
            u.Close();
            return true;
        }
        catch { return false; }
    }

    private static (int gamePort, int gatewayPort, int radiotowerPort) AllocatePorts()
    {
        for (int p = 52000; p < 60000; p += 10)
        {
            if (IsPortFree(p) && IsPortFree(p + 1) && IsPortFree(p + 2) && IsPortFree(p + 3) && IsPortFree(p + 4))
            {
                return (p, p + 2, p + 4);
            }
        }
        return (58191, 58190, 58192);
    }

    private static Process StartServer(string root, string saves, int gamePort, int gatewayPort, int radiotowerPort)
    {
        string dll = Path.Combine(root, "server", "bin", "Debug", "net9.0", "DurangoServer.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("ไม่พบ DurangoServer.dll", dll);
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.Combine(root, "server")
        };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--data"); psi.ArgumentList.Add(Path.Combine(root, "server", "data"));
        psi.ArgumentList.Add("--saves"); psi.ArgumentList.Add(saves);
        psi.ArgumentList.Add("--game-port"); psi.ArgumentList.Add(gamePort.ToString());
        psi.ArgumentList.Add("--gateway-port"); psi.ArgumentList.Add(gatewayPort.ToString());
        psi.ArgumentList.Add("--radiotower-port"); psi.ArgumentList.Add(radiotowerPort.ToString());
        psi.ArgumentList.Add("--enable-cheat");
        psi.ArgumentList.Add("--no-account-check");
        psi.ArgumentList.Add("--no-ip-bind");
        var process = Process.Start(psi);
        process.OutputDataReceived += (s, e) => { };
        process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine("[srv:err] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static bool WaitForPort(int port, int timeoutMs)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                using var socket = new TcpClient();
                socket.Connect("127.0.0.1", port);
                return true;
            }
            catch { Thread.Sleep(100); }
        }
        return false;
    }

    public static int Run(string host = null, int gamePort = 0, int gatewayPort = 0)
    {
        _passed = 0;
        _failed = 0;

        Process server = null;
        string tempSaves = null;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        if (gamePort == 0)
        {
            (gamePort, gatewayPort, int radiotowerPort) = AllocatePorts();
            host = "127.0.0.1";
            tempSaves = Path.Combine(Path.GetTempPath(), "durango_pet_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSaves);
            server = StartServer(root, tempSaves, gamePort, gatewayPort, radiotowerPort);
            if (!WaitForPort(gamePort, 15000) || !WaitForPort(gatewayPort, 15000))
            {
                Console.WriteLine("[FAIL] Server child process failed to open game or gateway port");
                try { server.Kill(); } catch { }
                return 1;
            }
        }

        try
        {
            Console.WriteLine($"=== pet system check: {host}:{gamePort} ===");
            string playerId = "pet_tester_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Connection conn = Connect(host, gamePort, gatewayPort, ref playerId, "PetTester");
            if (conn == null)
            {
                Console.WriteLine("[FAIL] Failed to connect test client");
                return 1;
            }

            // 1. Initial state: request pets info
            conn.Send(default(GetPetsInfo)); Pump(conn, 350);
            Check("pets info packet is received on startup", _petsInfo.HasValue);

            // 2. Add pet via cheat command
            conn.Send(new Cheat { _Cheat = "pet add trex 50" }); Pump(conn, 800);
            conn.Send(default(GetPetsInfo)); Pump(conn, 400);

            var petList = _petsInfo?.Pets.Data ?? Array.Empty<Messages.Pet>();
            Messages.Pet? trex = petList.FirstOrDefault(p => p.EntityType == 3004 || p.EntityType == 3005 || p.Name.Contains("티라노") || p.Name.Contains("T-Rex") || p.Name.Contains("trex"));
            if (!trex.HasValue && petList.Length > 0) trex = petList[0];
            Check("t-rex pet added and spawned", trex.HasValue && !string.IsNullOrEmpty(trex.Value.EntityId));
            string petId = trex?.EntityId ?? "";
            ushort petEntityType = trex?.EntityType ?? 3004;

            // 3. Verify AppearPet was received for the spawned pet
            Check("AppearPet received for spawned pet", _appearPet.HasValue && _appearPet.Value.EntityId == petId);

            // 4. Test Touch interaction wheel
            _touched = null;
            conn.Send(new Touch { EntityId = petId, EntityType = petEntityType, Tile = new Point2(0, 0) });
            Pump(conn, 500);
            bool hasMountInter = _touched?.Interactions?.Contains((int)Interaction.Mount) == true;
            bool hasFeedInter = _touched?.Interactions?.Contains((int)Interaction.Feeding) == true;
            bool hasReturnInter = _touched?.Interactions?.Contains((int)Interaction.ReturnPet) == true;
            Check("touch pet returns interaction wheel (Mount, Feeding, Return)", hasMountInter && hasFeedInter && hasReturnInter);

            // 5. Mount pet
            _display = null;
            conn.Send(new Cheat { _Cheat = "mount" }); Pump(conn, 600);
            Check("mounting pet updates PlayerDisplay to BoardingOn.Pet", _display?.BoardingOn == BoardingOn.Pet);

            // 6. Touch pet while mounted -> should offer Dismount
            _touched = null;
            conn.Send(new Touch { EntityId = petId, EntityType = petEntityType, Tile = new Point2(0, 0) });
            Pump(conn, 500);
            Check("touch while mounted offers Dismount", _touched?.Interactions?.Contains((int)Interaction.Dismount) == true);

            // 7. Unmount pet
            _display = null;
            conn.Send(new Cheat { _Cheat = "unmount" }); Pump(conn, 600);
            Check("unmounting pet restores BoardingOn to None", _display?.BoardingOn == BoardingOn.None);

            // 8. Test AI Follow Behavior: Player moves, pet should generate Move packet
            _petMoves.Clear();
            double now = Times.UnixTimeNow();
            conn.Send(new Move
            {
                EntityId = playerId,
                Movements = new[]
                {
                    new Movement
                    {
                        MotionName = "run",
                        MotionOption = 5,
                        PlaybackRate = 1f,
                        RotSpeed = 540f,
                        Path = new[]
                        {
                            new Location { Position = new WorldPosition(100f, 100f), Yaw = 45f, Time = now, Floor = 0, Height = 0f },
                            new Location { Position = new WorldPosition(600f, 600f), Yaw = 45f, Time = now + 1.0, Floor = 0, Height = 0f }
                        }
                    }
                }
            });
            Pump(conn, 1200);
            Check("pet follow AI emits Move packet when player walks away", _petMoves.Count > 0);

            // 9. Test Pet Stay mode
            conn.Send(new Cheat { _Cheat = "pet stay" }); Pump(conn, 400);
            _petMoves.Clear();
            now = Times.UnixTimeNow();
            conn.Send(new Move
            {
                EntityId = playerId,
                Movements = new[]
                {
                    new Movement
                    {
                        MotionName = "run",
                        MotionOption = 5,
                        PlaybackRate = 1f,
                        RotSpeed = 540f,
                        Path = new[]
                        {
                            new Location { Position = new WorldPosition(600f, 600f), Yaw = 90f, Time = now, Floor = 0, Height = 0f },
                            new Location { Position = new WorldPosition(1000f, 600f), Yaw = 90f, Time = now + 1.0, Floor = 0, Height = 0f }
                        }
                    }
                }
            });
            Pump(conn, 1000);
            Check("pet stay mode halts follow movement", _petMoves.Count == 0);

            // 10. Test Pet Follow mode
            conn.Send(new Cheat { _Cheat = "pet follow" }); Pump(conn, 1200);
            Check("pet follow mode resumes following player", _petMoves.Count > 0);

            // 11. Test Feeding System
            conn.Send(new Cheat { _Cheat = "give meat 3" }); Pump(conn, 500);
            _feedingSuccess = null;
            conn.Send(new Feeding { PetId = petId, FoodIds = null }); Pump(conn, 800);
            Check("feeding pet succeeds with FeedingSuccess", _feedingSuccess.HasValue && _feedingSuccess.Value.PetId == petId);

            // 12. Test Persistence across reconnect
            conn.Close();
            Thread.Sleep(1000);

            _petsInfo = null;
            _appearPet = null;
            Connection reconn = Connect(host, gamePort, gatewayPort, ref playerId, "PetTester");
            if (reconn != null)
            {
                reconn.Send(default(GetPetsInfo)); Pump(reconn, 500);
                var reloadedPets = _petsInfo?.Pets.Data ?? Array.Empty<Messages.Pet>();
                bool petSurvives = reloadedPets.Any(p => p.EntityId == petId);
                Check("pet data and state survive disconnect and reconnect", petSurvives);
                reconn.Close();
            }
            else
            {
                Check("pet data and state survive disconnect and reconnect", false, "reconnect failed");
            }

            Console.WriteLine($"=== pet check result: PASS {_passed}, FAIL {_failed} ===");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            if (server != null)
            {
                try { server.Kill(); } catch { }
                try { server.WaitForExit(3000); } catch { }
            }
            if (!string.IsNullOrEmpty(tempSaves) && Directory.Exists(tempSaves))
            {
                try { Directory.Delete(tempSaves, true); } catch { }
            }
        }
    }
}
