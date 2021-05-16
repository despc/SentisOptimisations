using Torch;

namespace SentisOptimisationsPlugin
{
    public class Config : ViewModel
    {
        private double _contractEscortMultiplier = 10;
        private double _contractAcquisitionMultiplier = 15; //Доставка
        private double _contractHaulingtMultiplier = 10; //Перевозка
        private double _contractRepairMultiplier = 10;
        private double _contractFindMultiplier = 30;

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
    }
}