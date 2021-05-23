using Torch;

namespace SentisOptimisationsPlugin
{
    public class Config : ViewModel
    {
        private double _contractEscortMultiplier = 10;
        private double _contractAcquisitionMultiplier = 30; //Доставка
        private double _contractHaulingtMultiplier = 10; //Перевозка
        private double _contractRepairMultiplier = 10;
        private double _contractFindMultiplier = 25;
        private bool _enabledPcuLimiter = true;
        private int _maxStaticGridPCU = 100000;
        private int _maxDinamycGridPCU = 30000;
        private bool _allowProjection = true;
        private bool _allowMerge = false;
        private bool _includeConnectedGrids = false;

        public double ContractEscortMultiplier
        {
            get => _contractEscortMultiplier;
            set => SetValue(ref _contractEscortMultiplier, value);
        }

        public double ContractAcquisitionMultiplier
        {
            get => _contractAcquisitionMultiplier;
            set => SetValue(ref _contractAcquisitionMultiplier, value);
        }

        public double ContractHaulingtMultiplier
        {
            get => _contractHaulingtMultiplier;
            set => SetValue(ref _contractHaulingtMultiplier, value);
        }

        public double ContractRepairMultiplier
        {
            get => _contractRepairMultiplier;
            set => SetValue(ref _contractRepairMultiplier, value);
        }

        public double ContractFindMultiplier
        {
            get => _contractFindMultiplier;
            set => SetValue(ref _contractFindMultiplier, value);
        }

        public bool EnabledPcuLimiter
        {
            get => _enabledPcuLimiter;
            set => SetValue(ref _enabledPcuLimiter, value);
        }

        public int MaxStaticGridPCU
        {
            get => _maxStaticGridPCU;
            set => SetValue(ref _maxStaticGridPCU, value);
        }

        public int MaxDinamycGridPCU
        {
            get => _maxDinamycGridPCU;
            set => SetValue(ref _maxDinamycGridPCU, value);
        }

        public bool AllowProjection
        {
            get => _allowProjection;
            set => SetValue(ref _allowProjection, value);
        }

        public bool AllowMerge
        {
            get => _allowMerge;
            set => SetValue(ref _allowMerge, value);
        }

        public bool IncludeConnectedGrids
        {
            get => _includeConnectedGrids;
            set => SetValue(ref _includeConnectedGrids, value);
        }
    }
}