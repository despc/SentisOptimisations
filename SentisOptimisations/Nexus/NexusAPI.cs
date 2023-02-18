
using ProtoBuf;
using System.Collections.Generic;
using VRage.Game;
using VRageMath;

namespace SentisOptimisationsPlugin
{
  public class NexusAPI
  {
    public ushort CrossServerModID;

    public NexusAPI(ushort SocketID) => this.CrossServerModID = SocketID;

    public static bool IsRunningNexus() => false;

    public static bool IsPlayerOnline(long IdentityID) => false;

    private static List<object[]> GetSectorsObject() => new List<object[]>();

    private static List<object[]> GetAllOnlinePlayersObject() => new List<object[]>();

    private static List<object[]> GetAllServersObject() => new List<object[]>();

    private static List<object[]> GetAllOnlineServersObject() => new List<object[]>();

    private static object[] GetThisServerObject() => new object[6];

    public static NexusAPI.Server GetThisServer()
    {
      object[] thisServerObject = NexusAPI.GetThisServerObject();
      return new NexusAPI.Server((string) thisServerObject[0], (int) thisServerObject[1], (int) (short) thisServerObject[2], (float) (int) thisServerObject[3], (int) thisServerObject[4], (List<ulong>) thisServerObject[5]);
    }

    public static List<NexusAPI.Sector> GetSectors()
    {
      List<object[]> sectorsObject = NexusAPI.GetSectorsObject();
      List<NexusAPI.Sector> sectors = new List<NexusAPI.Sector>();
      foreach (object[] objArray in sectorsObject)
        sectors.Add(new NexusAPI.Sector((string) objArray[0], (string) objArray[1], (int) objArray[2], (bool) objArray[3], (Vector3D) objArray[4], (double) objArray[5], (int) objArray[6]));
      return sectors;
    }

    public static int GetServerIDFromPosition(Vector3D Position) => 0;

    public static List<NexusAPI.Player> GetAllOnlinePlayers()
    {
      List<object[]> onlinePlayersObject = NexusAPI.GetAllOnlinePlayersObject();
      List<NexusAPI.Player> allOnlinePlayers = new List<NexusAPI.Player>();
      foreach (object[] objArray in onlinePlayersObject)
        allOnlinePlayers.Add(new NexusAPI.Player((string) objArray[0], (ulong) objArray[1], (long) objArray[2], (int) objArray[3]));
      return allOnlinePlayers;
    }

    public static List<NexusAPI.Server> GetAllServers()
    {
      List<object[]> allServersObject = NexusAPI.GetAllServersObject();
      List<NexusAPI.Server> allServers = new List<NexusAPI.Server>();
      foreach (object[] objArray in allServersObject)
        allServers.Add(new NexusAPI.Server((string) objArray[0], (int) objArray[1], (int) objArray[2], (string) objArray[3]));
      return allServers;
    }

    public static List<NexusAPI.Server> GetAllOnlineServers()
    {
      List<object[]> onlineServersObject = NexusAPI.GetAllOnlineServersObject();
      List<NexusAPI.Server> allOnlineServers = new List<NexusAPI.Server>();
      foreach (object[] objArray in onlineServersObject)
        allOnlineServers.Add(new NexusAPI.Server((string) objArray[0], (int) objArray[1], (int) objArray[2], (float) objArray[3], (int) objArray[4], (List<ulong>) objArray[5]));
      return allOnlineServers;
    }

    public static bool IsServerOnline(int ServerID) => false;

    public static void BackupGrid(
      List<MyObjectBuilder_CubeGrid> GridObjectBuilders,
      long OnwerIdentity)
    {
    }

    public static void SendChatMessageToDiscord(ulong ChannelID, string Author, string Message)
    {
    }

    public static void SendEmbedMessageToDiscord(
      ulong ChannelID,
      string EmbedTitle,
      string EmbedMsg,
      string EmbedFooter,
      string EmbedColor = null)
    {
    }

    public void SendMessageToServer(int ServerID, byte[] Message)
    {
    }

    public void SendMessageToAllServers(byte[] Message)
    {
    }

    public class Sector
    {
      public readonly string Name;
      public readonly string IPAddress;
      public readonly int Port;
      public readonly bool IsGeneralSpace;
      public readonly Vector3D Center;
      public readonly double Radius;
      public readonly int ServerID;

      public Sector(
        string Name,
        string IPAddress,
        int Port,
        bool IsGeneralSpace,
        Vector3D Center,
        double Radius,
        int ServerID)
      {
        this.Name = Name;
        this.IPAddress = IPAddress;
        this.Port = Port;
        this.IsGeneralSpace = IsGeneralSpace;
        this.Center = Center;
        this.Radius = Radius;
        this.ServerID = ServerID;
      }
    }

    public class Player
    {
      public readonly string PlayerName;
      public readonly ulong SteamID;
      public readonly long IdentityID;
      public readonly int OnServer;

      public Player(string PlayerName, ulong SteamID, long IdentityID, int OnServer)
      {
        this.PlayerName = PlayerName;
        this.SteamID = SteamID;
        this.IdentityID = IdentityID;
        this.OnServer = OnServer;
      }
    }

    public class Server
    {
      public readonly string Name;
      public readonly int ServerID;
      public readonly int ServerType;
      public readonly string ServerIP;
      public readonly int MaxPlayers;
      public readonly float ServerSS;
      public readonly int TotalGrids;
      public readonly List<ulong> ReservedPlayers;

      public Server(string Name, int ServerID, int ServerType, string IP)
      {
        this.Name = Name;
        this.ServerID = ServerID;
        this.ServerType = ServerType;
        this.ServerIP = IP;
      }

      public Server(
        string Name,
        int ServerID,
        int MaxPlayers,
        float SimSpeed,
        int TotalGrids,
        List<ulong> ReservedPlayers)
      {
        this.Name = Name;
        this.ServerID = ServerID;
        this.MaxPlayers = MaxPlayers;
        this.ServerSS = SimSpeed;
        this.TotalGrids = TotalGrids;
        this.ReservedPlayers = ReservedPlayers;
      }
    }

    [ProtoContract]
    public class CrossServerMessage
    {
      [ProtoMember(1)]
      public readonly int ToServerID;
      [ProtoMember(2)]
      public readonly int FromServerID;
      [ProtoMember(3)]
      public readonly ushort UniqueMessageID;
      [ProtoMember(4)]
      public readonly byte[] Message;

      public CrossServerMessage(
        ushort UniqueMessageID,
        int ToServerID,
        int FromServerID,
        byte[] Message)
      {
        this.UniqueMessageID = UniqueMessageID;
        this.ToServerID = ToServerID;
        this.FromServerID = FromServerID;
        this.Message = Message;
      }

      public CrossServerMessage()
      {
      }
    }
  }
}
