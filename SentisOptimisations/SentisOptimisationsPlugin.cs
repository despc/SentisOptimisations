using NLog;
using Torch;
using Torch.API;

namespace FixTurrets
{
    public class FixTurretsPlugin : TorchPluginBase
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public override void Init(ITorchBase torch)
        {
            Log.Info("Init FixTurretsPlugin");
        }
    }
}