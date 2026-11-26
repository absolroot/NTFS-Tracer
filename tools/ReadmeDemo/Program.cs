using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JournalDesk;

class Capture
{
    const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    static void Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var input = Path.Combine(root, "demo", "$J");
        var settings = Path.Combine(root, "settings");
        Directory.CreateDirectory(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        File.WriteAllText(Path.Combine(settings, "language.txt"), "en");
        File.WriteAllText(Path.Combine(settings, "appearance.txt"), "dark");
        typeof(App).Assembly.GetType("JournalDesk.DeskSettings")!.GetProperty("DirectoryPath", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        using (var output = new BinaryWriter(File.Create(input)))
        {
            var timestamp = new DateTime(2026, 1, 15, 13, 0, 0, DateTimeKind.Utc);
            long file = 42 | (1L << 48);
            void Record(string name, long id, uint reason, int second)
            {
                var nameBytes = Encoding.Unicode.GetBytes(name);
                int size = (60 + nameBytes.Length + 7) & ~7;
                var offset = output.BaseStream.Position;
                output.Write(size); output.Write((ushort)2); output.Write((ushort)0);
                output.Write(id); output.Write(5L | (1L << 48)); output.Write(offset);
                output.Write(timestamp.AddSeconds(second).ToFileTimeUtc()); output.Write(reason);
                output.Write(0); output.Write(0); output.Write(32);
                output.Write((ushort)nameBytes.Length); output.Write((ushort)60);
                output.Write(nameBytes); output.Write(new byte[size - 60 - nameBytes.Length]);
            }
                        string[] names = ["meeting-notes", "budget", "inventory", "architecture", "release-plan", "case-notes", "project-brief", "worklog"];
            for (int i = 0; i < 128; i++)
            {
                long id = (1L << 48) + 100 + i;
                string prefix = i % 16 == 0 ? "report" : names[i % names.Length];
                string name = $"{prefix}-{i + 1:000}.{(i % 3 == 0 ? "xlsx" : i % 3 == 1 ? "docx" : "pdf")}";
                Record(name, id, 0x100, -129 + i);
                Record(name, id, 0x2, -128 + i);
                Record(name, id, 0x80000000, -127 + i);
            }
            Record("sdelete64.exe", file + 3, 0x100, -2);
            Record("SDELETE64.EXE-A1B2C3D4.pf", file + 4, 0x100, -1);
            Record("report-draft.docx", file, 0x100, 0);
            Record("case-notes.txt", file + 1, 0x100, 1);
            Record("report-draft.docx", file, 0x2, 2);
            Record("timeline.csv", file + 2, 0x100, 3);
            Record("report-draft.docx", file, 0x1000, 4);
            Record("report-final.docx", file, 0x2000, 4);
            Record("report-final.docx", file, 0x80000000, 5);
        }
        File.WriteAllText(Path.Combine(settings, "details-position.txt"), "right");
        var app = new App();
        app.InitializeComponent();
        typeof(App).GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [true]);
        var window = new MainWindow { Width = 1120, Height = 680, Left = -12000, Top = -12000, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        var content = (FrameworkElement)window.Content;
        void Save(string name)
        {
            content.Measure(new Size(1120, 680)); content.Arrange(new Rect(0, 0, 1120, 680)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1120, 680, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var draw = background.RenderOpen()) draw.DrawRectangle(window.Background, null, new Rect(0, 0, 1120, 680));
            bitmap.Render(background); bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(root, name + ".png")); png.Save(stream);
        }
        window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var global = (FrameworkElement)window.FindName("GlobalView");
                var grid = (DataGrid)global.FindName("Records");
                await (Task)typeof(MainWindow).GetMethod("OpenJournal", Hidden)!.Invoke(window, [input, null])!;
                ((TabControl)window.FindName("Views")).SelectedItem = window.FindName("GlobalTab"); await Task.Delay(850);
                Console.WriteLine($"Rows: {grid.Items.Count}");
                if (grid.Items.Count < 100) throw new InvalidOperationException("Expected a populated journal.");
                var coordinates = new List<string>();
                void Mark(string name, FrameworkElement element, double x = -1, double y = -1)
                {
                    content.UpdateLayout();
                    var point = element.TranslatePoint(new Point(x < 0 ? element.ActualWidth / 2 : x, y < 0 ? element.ActualHeight / 2 : y), content);
                    coordinates.Add(FormattableString.Invariant($"{name} {point.X:0} {point.Y:0}"));
                }
                var search = (TextBox)global.FindName("QueryText");
                Mark("search", search, 34);
                Save("00-all");
                foreach (var inputText in new[] { "re", "repo", "report" })
                {
                    search.Text = inputText;
                    await Task.Delay(540);
                    Save(inputText);
                }
                if (grid.Items.Count < 20 || grid.Items.Count > 40) throw new InvalidOperationException($"Unexpected search count: {grid.Items.Count}");
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
                Mark("result", row, 300);
                grid.SelectedIndex = 1;
                await Task.Delay(350);
                Save("01-search");
                var selectedResult = grid.SelectedItem;
                var history = (Button)global.FindName("GoFile");
                Mark("history", history);
                history.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, history));
                await Task.Delay(700);
                var eventsGrid = (DataGrid)window.FindName("Events");
                if (eventsGrid.Items.Count < 4) throw new InvalidOperationException("Expected the target file's activity history.");
                Save("01b-open-history");
                var rename = (DataGridRow)eventsGrid.ItemContainerGenerator.ContainerFromIndex(2);
                Mark("rename", rename, 300);
                eventsGrid.SelectedIndex = 2;
                await Task.Delay(300);
                Save("02-history");
                var proof = (ListView)window.FindName("ProofPicker");
                if (proof.Items.Count != 2) throw new InvalidOperationException("Expected both sides of the rename.");
                Mark("old", (ListViewItem)proof.ItemContainerGenerator.ContainerFromIndex(0), 100);
                Mark("new", (ListViewItem)proof.ItemContainerGenerator.ContainerFromIndex(1), 100);
                proof.SelectedIndex = 0;
                await Task.Delay(260);
                Save("old-name");
                proof.SelectedIndex = 1;
                await Task.Delay(260);
                var hex = (TabItem)window.FindName("OriginalBytesTab");
                Mark("hex", hex);
                ((TabControl)window.FindName("EvidenceTabs")).SelectedItem = hex;
                await Task.Delay(350);
                Save("03-hex");
                var back = (Button)window.FindName("BackToLog");
                Mark("back", back);
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, back));
                await Task.Delay(250);
                if (!ReferenceEquals(selectedResult, grid.SelectedItem) || search.Text != "report")
                    throw new InvalidOperationException("Returning from file history lost the search result.");
                Save("08-return");
                var views = (TabControl)window.FindName("Views");
                var logTab = (TabItem)window.FindName("TransactionTab");
                Mark("transactions", logTab);
                views.SelectedItem = logTab;
                var log = (TransactionView)window.FindName("Transactions");
                await log.OpenSourceAsync(Path.Combine(root, "$LogFile"));
                await Task.Delay(450);
                var txRows = (DataGrid)log.FindName("Rows");
                if (txRows.Items.Count < 10) throw new InvalidOperationException("Expected synthetic transaction records.");
                txRows.SelectedIndex = -1;
                await Task.Delay(180);
                Save("04-transactions");
                Mark("txrow", (DataGridRow)txRows.ItemContainerGenerator.ContainerFromIndex(2), 140);
                txRows.SelectedIndex = 2;
                await Task.Delay(550);
                Save("05-timestamps");
                var leadsTab = (TabItem)window.FindName("LeadsTab");
                Mark("leads", leadsTab);
                views.SelectedItem = leadsTab;
                await Task.Delay(200);
                var usn = (TabItem)window.FindName("UsnLeads");
                usn.IsSelected = true;
                await Task.Delay(250);
                var leads = (InvestigationView)usn.Content;
                var rule = (ComboBox)leads.FindName("Rules");
                for (int i = 0; i < 80 && !rule.IsEnabled; i++) await Task.Delay(50);
                rule.SelectedIndex = Convert.ToInt32(Enum.Parse(typeof(App).Assembly.GetType("JournalDesk.LeadKind")!, "ToolName"));
                await Task.Delay(450);
                var candidates = (DataGrid)leads.FindName("Leads");
                if (candidates.Items.Count != 2) throw new InvalidOperationException($"Expected two lead rows, saw {candidates.Items.Count}.");
                candidates.SelectedIndex = -1;
                await Task.Delay(150);
                Save("06-leads");
                Mark("leadrow", (DataGridRow)candidates.ItemContainerGenerator.ContainerFromIndex(1), 240);
                candidates.SelectedIndex = 1;
                await Task.Delay(180);
                Save("07-lead-evidence");
                File.WriteAllLines(Path.Combine(root, "clicks.txt"), coordinates);
                Console.WriteLine($"Search matches: {grid.Items.Count}; history events: {eventsGrid.Items.Count}");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
        });
        System.Windows.Threading.Dispatcher.Run();
    }
}
