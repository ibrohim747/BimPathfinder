using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace BimPathfinder
{
    public partial class PathfinderDialog : Window
    {
        private readonly Document _doc;

        private List<PipeType> _pipeTypes;
        private List<DuctType> _ductTypes;
        private List<double> _currentDiametersMm;

        // FIXED: initial value is true — suppress ALL events
        // during InitializeComponent(); lifted only in OnLoaded.
        private bool _suppressEvents = true;

        public double MaxPierceThicknessMm { get; private set; }
        public double DiameterMm { get; private set; } = 100;
        public double GridStepMm { get; private set; } = 200;
        public MepSystemType SystemType { get; private set; } = MepSystemType.Pipe;
        public PipeType SelectedPipeType { get; private set; }
        public DuctType SelectedDuctType { get; private set; }
        public PathfinderSettings Settings { get; private set; } = new PathfinderSettings();
        public bool Confirmed { get; private set; }

        public PathfinderDialog(Document doc)
        {
            // _doc and _suppressEvents=true are set BEFORE InitializeComponent.
            // This guarantees that any SelectionChanged/events fired by the XAML parser
            // are ignored and do not cause a NullReferenceException on _doc.
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));

            InitializeComponent(); // WPF may fire events here — _suppressEvents=true protects us

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Lift the suppression flag and load data once the UI is fully
            // initialized and _doc is valid.
            _suppressEvents = false;
            LoadPipeTypes();
        }

        // ─────────────────────────────────────────────────────────────
        // Load data from Revit
        // ─────────────────────────────────────────────────────────────

        private void LoadPipeTypes()
        {
            _suppressEvents = true;

            _pipeTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .OrderBy(t => t.Name)
                .ToList();

            CbMepType.ItemsSource = _pipeTypes;
            CbMepType.DisplayMemberPath = "Name";
            CbMepType.SelectedIndex = _pipeTypes.Count > 0 ? 0 : -1;

            _suppressEvents = false;

            if (_pipeTypes.Count > 0)
                LoadDiametersForPipeType(_pipeTypes[0]);
        }

        private void LoadDuctTypes()
        {
            _suppressEvents = true;

            _ductTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .OrderBy(t => t.Name)
                .ToList();

            CbMepType.ItemsSource = _ductTypes;
            CbMepType.DisplayMemberPath = "Name";
            CbMepType.SelectedIndex = _ductTypes.Count > 0 ? 0 : -1;

            _suppressEvents = false;

            PopulateDiameterCombo(new List<double>
            {
                100, 150, 200, 250, 300, 350, 400, 450,
                500, 600, 700, 800, 900, 1000, 1200
            });
        }

        private void LoadDiametersForPipeType(PipeType pipeType)
        {
            var diamsMm = GetDiametersFromSegments(pipeType);
            if (diamsMm.Count == 0)
                diamsMm = GetStandardPipeDiameters();
            PopulateDiameterCombo(diamsMm);
        }

        private List<double> GetDiametersFromSegments(PipeType pipeType)
        {
            var result = new HashSet<double>();
            try
            {
                var rpManager = pipeType.RoutingPreferenceManager;
                int ruleCount = rpManager.GetNumberOfRules(
                    RoutingPreferenceRuleGroupType.Segments);

                for (int i = 0; i < ruleCount; i++)
                {
                    var rule = rpManager.GetRule(
                        RoutingPreferenceRuleGroupType.Segments, i);
                    var segment = _doc.GetElement(rule.MEPPartId) as PipeSegment;
                    if (segment == null) continue;

                    foreach (MEPSize size in segment.GetSizes())
                    {
                        if (size.NominalDiameter <= 0) continue;
                        double nomDiamMm = size.NominalDiameter * 304.8;
                        result.Add(Math.Round(nomDiamMm, 1));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BimPathfinder] GetDiametersFromSegments: {ex.Message}");
            }
            return result.OrderBy(d => d).ToList();
        }

        private static List<double> GetStandardPipeDiameters()
            => new List<double>
               { 15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400, 500 };

        private void PopulateDiameterCombo(List<double> diamsMm)
        {
            _currentDiametersMm = diamsMm;
            _suppressEvents = true;

            CbDiameter.Items.Clear();
            foreach (double d in diamsMm)
                CbDiameter.Items.Add($"{d:0.#} mm");

            // Select the value closest to the current one
            if (double.TryParse(TbDiameter.Text.Replace(',', '.'),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double cur)
                && diamsMm.Count > 0)
            {
                int idx = diamsMm.IndexOf(diamsMm.OrderBy(d => Math.Abs(d - cur)).First());
                CbDiameter.SelectedIndex = idx;
            }
            else if (diamsMm.Count > 0)
            {
                CbDiameter.SelectedIndex = 0;
            }

            _suppressEvents = false;
        }

        // ─────────────────────────────────────────────────────────────
        // UI event handlers
        // ─────────────────────────────────────────────────────────────

        private void OnSystemTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            if (CbSystem.SelectedIndex == 0)
            {
                LblMepType.Text = "Pipe type:";
                LoadPipeTypes();
            }
            else
            {
                LblMepType.Text = "Duct type:";
                LoadDuctTypes();
            }
        }

        private void OnMepTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (CbSystem.SelectedIndex != 0) return;

            var pipeType = CbMepType.SelectedItem as PipeType;
            if (pipeType != null)
                LoadDiametersForPipeType(pipeType);
        }

        private void OnDiameterSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            int idx = CbDiameter.SelectedIndex;
            if (idx >= 0 && _currentDiametersMm != null && idx < _currentDiametersMm.Count)
            {
                TbDiameter.Text = _currentDiametersMm[idx].ToString("0.#",
                    CultureInfo.InvariantCulture);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Confirm and cancel
        // ─────────────────────────────────────────────────────────────

        private void OnOkClick(object sender, RoutedEventArgs e)
        {
            if (!TryParseDouble(TbMaxPierce.Text, out double pierce, min: 0))
            { Error("Invalid MaxPierceThickness value.\nEnter a number ≥ 0."); return; }

            if (!TryParseDouble(TbDiameter.Text, out double diam, min: 1))
            { Error("Invalid diameter.\nEnter a number > 0."); return; }

            if (!TryParseDouble(TbGridStep.Text, out double step, min: 50))
            { Error("Grid step must be at least 50 mm."); return; }

            TryParseDouble(TbPenaltyTurn.Text, out double pt, min: 0);
            TryParseDouble(TbPenaltySupport.Text, out double ps, min: 0);
            TryParseDouble(TbPenaltyPierce.Text, out double pp, min: 0);

            MaxPierceThicknessMm = pierce;
            DiameterMm = diam;
            GridStepMm = step;

            SystemType = CbSystem.SelectedIndex == 0 ? MepSystemType.Pipe : MepSystemType.Duct;
            SelectedPipeType = CbMepType.SelectedItem as PipeType;
            SelectedDuctType = CbMepType.SelectedItem as DuctType;

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

        // ─────────────────────────────────────────────────────────────
        private static bool TryParseDouble(string text, out double value,
                                           double min = double.MinValue)
        {
            bool ok = double.TryParse(
                text?.Replace(',', '.'),
                NumberStyles.Any, CultureInfo.InvariantCulture,
                out value);
            return ok && value >= min;
        }

        private static void Error(string msg)
            => MessageBox.Show(msg, "BIM-Pathfinder — Input Error",
                               MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}