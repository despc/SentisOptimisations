
using SentisOptimisations;
using Torch;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace SentisOptimisationsPlugin
{
  [Category("so")]
  public class AdminCommands : CommandModule
  {
    [Command("reload", ".", null)]
    [Permission(MyPromoteLevel.Moderator)]
    public void Reload()
    {
      SentisOptimisationsPlugin.Config = ConfigUtils.Load<Config>((TorchPluginBase) SentisOptimisationsPlugin.Instance, "SentisOptimisations.cfg");
      this.Context.Respond("Configuration reloaded.");
    }

    [Command("config", ".", null)]
    [Permission(MyPromoteLevel.Moderator)]
    public void Config()
    {
      this.Context.Respond("Sentis Optimisations Config:");
      this.Context.Respond(string.Format("> Enabled: {0}", (object) SentisOptimisationsPlugin.Config.EnabledPcuLimiter));
      this.Context.Respond(string.Format("> MaxStaticGridPCU: {0}", (object) SentisOptimisationsPlugin.Config.MaxStaticGridPCU));
      this.Context.Respond(string.Format("> MaxDynamicGridPCU: {0}", (object) SentisOptimisationsPlugin.Config.MaxDinamycGridPCU));
      this.Context.Respond(string.Format("> AllowProjection: {0}", (object) SentisOptimisationsPlugin.Config.AllowProjection));
      this.Context.Respond(string.Format("> AllowMerge: {0}", (object) SentisOptimisationsPlugin.Config.AllowMerge));
      this.Context.Respond(string.Format("> CountConnectorDocking: {0}", (object) SentisOptimisationsPlugin.Config.IncludeConnectedGrids));
    }
  }
}
