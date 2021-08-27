using System;
using Torch;

namespace SentisOptimisationsPlugin
{
    public class ConfigShipInMarket : ViewModel
    {

        private String _shipName = "Nagibator9000.sbc";

        private long _shipyardId = 0000000000000;
        
        public String ShipName { get => _shipName; set => SetValue(ref _shipName, value); }
        
        public long ShipyardId { get => _shipyardId; set => SetValue(ref _shipyardId, value); }
        
    }
}