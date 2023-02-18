using System.Windows.Controls;

namespace SOPlugin.GUI
{
    partial class ConfigGUI : UserControl
    {
        public ConfigGUI()
        {
            InitializeComponent();
            MainFilteredGrid.DataContext = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
        }
    }
}