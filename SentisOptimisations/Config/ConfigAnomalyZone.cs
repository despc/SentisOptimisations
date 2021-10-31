using System;
using System.Collections.ObjectModel;
using Torch;

namespace SentisOptimisationsPlugin
{
    public class ConfigAnomalyZone : ViewModel
    {
        private long _blockId;
        private long _lastWeekWinnerFactionId;
        private DateTime _lastWeekWinnerSavedTime;
        
        ObservableCollection<ConfigAnomalyZonePoints> _points = new ObservableCollection<ConfigAnomalyZonePoints>();
        
        public ObservableCollection<ConfigAnomalyZonePoints> Points

        {
            get { return _points; }
            set
            {
                _points.Clear();
                foreach (ConfigAnomalyZonePoints points in value)
                {
                    _points.Add(points);
                }
            }
        }
        public long BlockId { get => _blockId; set => SetValue(ref _blockId, value); }
        public long LastWeekWinnerFactionId { get => _lastWeekWinnerFactionId; set => SetValue(ref _lastWeekWinnerFactionId, value); }
        public DateTime LastWeekWinnerSavedTime { get => _lastWeekWinnerSavedTime; set => SetValue(ref _lastWeekWinnerSavedTime, value); }
        
    }
    
    public class ConfigAnomalyZonePoints : ViewModel
    {
        private long _factionId;
        private int _points = 0;
        public long FactionId { get => _factionId; set => SetValue(ref _factionId, value); }
        public int Points
        {
            get => _points;
            set
            {
                SetValue(ref _points, value);
                OnPropertyChanged();
            }
        }
    }
}