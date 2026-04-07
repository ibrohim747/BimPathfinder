using System;
using System.Globalization;
using System.Windows;

namespace BimPathfinder
{
    public partial class PathfinderDialog : Window
    {
        // ── Результаты (читаются снаружи после ShowDialog) ───────────
        public double MaxPierceThicknessMm { get; private set; }
        public double DiameterMm { get; private set; } = 100;
        public double GridStepMm { get; private set; } = 200;
        public MepSystemType SystemType { get; private set; } = MepSystemType.Pipe;
        public PathfinderSettings Settings { get; private set; } = new PathfinderSettings();
        public bool Confirmed { get; private set; }

        // ─────────────────────────────────────────────────────────────
        public PathfinderDialog()
        {
            InitializeComponent();
        }

        // ── Обработчики кнопок ───────────────────────────────────────
        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (!TryParseDouble(TbMaxPierce.Text, out double pierce, min: 0))
            {
                Error("Некорректное значение MaxPierceThickness.\nВведите число ≥ 0.");
                return;
            }

            if (!TryParseDouble(TbDiameter.Text, out double diam, min: 1))
            {
                Error("Некорректный диаметр.\nВведите число > 0.");
                return;
            }

            if (!TryParseDouble(TbGridStep.Text, out double step, min: 50))
            {
                Error("Шаг сетки должен быть не менее 50 мм.");
                return;
            }

            TryParseDouble(TbPenaltyTurn.Text, out double pt, min: 0);
            TryParseDouble(TbPenaltySupport.Text, out double ps, min: 0);
            TryParseDouble(TbPenaltyPierce.Text, out double pp, min: 0);

            MaxPierceThicknessMm = pierce;
            DiameterMm = diam;
            GridStepMm = step;

            SystemType = CbSystem.SelectedIndex == 0
                ? MepSystemType.Pipe
                : MepSystemType.Duct;

            Settings = new PathfinderSettings
            {
                PenaltyTurn = pt > 0 ? pt : 2.0,
                PenaltySupport = ps > 0 ? ps : 0.5,
                PenaltyPierce = pp > 0 ? pp : 10.0,
            };

            Confirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        // ── Вспомогательные ─────────────────────────────────────────
        private static bool TryParseDouble(string text, out double value, double min = double.MinValue)
        {
            bool ok = double.TryParse(
                text.Replace(',', '.'),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out value);
            return ok && value >= min;
        }

        private static void Error(string msg)
            => MessageBox.Show(msg, "BIM-Pathfinder — Ошибка ввода",
                               MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}