using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SOPlugin.GUI
{
    public class ConfigGUI : UserControl
    {
        // Previously generated from ConfigGUI.xaml; now built in managed code.
        internal TextBlock ClustersStatistic;
        internal TextBlock FreezerStatistic;
        internal TextBlock ScriptsStatistic;
        internal Expander ScriptsExpander;
        internal FilteredGrid MainFilteredGrid;

        public ConfigGUI()
        {
            BuildUi();
            MainFilteredGrid.DataContext = SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config;
        }

        private void BuildUi()
        {
            var root = new StackPanel { Orientation = Orientation.Vertical };

            var clustersRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(3, 3, 3, 20)
            };
            clustersRow.Children.Add(new TextBlock { Text = "Clusters:" });
            ClustersStatistic = new TextBlock
            {
                Margin = new Thickness(3, 0, 0, 0),
                Width = double.NaN,
                Text = "Clusters statistic will be here",
                FontWeight = FontWeights.Bold
            };
            clustersRow.Children.Add(ClustersStatistic);
            root.Children.Add(clustersRow);

            var freezerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(3, 3, 3, 20)
            };
            freezerRow.Children.Add(new TextBlock { Text = "Freezer Statistic:" });
            FreezerStatistic = new TextBlock
            {
                Margin = new Thickness(3, 0, 0, 0),
                Width = double.NaN,
                Text = "Freezer statistic will be here",
                FontWeight = FontWeights.Bold
            };
            freezerRow.Children.Add(FreezerStatistic);
            root.Children.Add(freezerRow);

            // One line while it is closed, a line per script when it is opened.
            ScriptsStatistic = new TextBlock
            {
                Margin = new Thickness(3, 3, 3, 3),
                Width = double.NaN,
                FontFamily = new FontFamily("Consolas"),
                Text = "Script statistic will be here"
            };
            ScriptsExpander = new Expander
            {
                Margin = new Thickness(3, 3, 3, 20),
                Header = "Scripts:",
                IsExpanded = false,
                Content = new ScrollViewer
                {
                    MaxHeight = 220,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = ScriptsStatistic
                }
            };
            root.Children.Add(ScriptsExpander);

            MainFilteredGrid = new FilteredGrid();
            root.Children.Add(MainFilteredGrid);

            Content = root;
        }
    }
}
