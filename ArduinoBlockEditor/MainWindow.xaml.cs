#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Python.Runtime;

namespace ArduinoBlockEditor
{
    public partial class MainWindow : Window
    {
        private dynamic pyModel;
        private UIElement draggingElement = null;
        private dynamic draggingConfig = null;
        private readonly List<dynamic> draggingBlockGroup = new();
        private bool isPaletteDrag = false;

        // ドラッグ元情報
        private string sourceSection = null;
        private int sourceIndex = -1;
        private dynamic sourceSlotRef = null;
        private string sourceSlotKey = null;
        private dynamic sourceSlotDefault = null;

        // ドロップホバー（ハイライト）用変数
        private DropTarget lastHoverTarget = null;
        private readonly Border insertionIndicator = new Border
        {
            Height = 4,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3182CE")),
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(2)
        };

        private class DropTarget
        {
            public string Type { get; set; } = "";
            public dynamic TargetList { get; set; } = null;
            public StackPanel ContainerPanel { get; set; } = null;
            public int CalculatedIndex { get; set; }

            public dynamic SlotRef { get; set; } = null;
            public string SlotKey { get; set; } = "";
            public FrameworkElement Element { get; set; } = null;
        }

        private readonly List<DropTarget> activeDropTargets = new();

        private readonly Dictionary<string, SolidColorBrush> CategoryColors = new()
        {
            { "入出力・時間", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B73AF")) },
            { "制御構文",     new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D35400")) },
            { "演算・計算",     new SolidColorBrush((Color)ColorConverter.ConvertFromString("#73368C")) },
            { "変数・代入",     new SolidColorBrush((Color)ColorConverter.ConvertFromString("#218C4E")) },
            { "関数",         new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B7950B")) },
            { "シリアル通信", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#117A65")) },
            { "その他",       new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A90E2")) }
        };

        public MainWindow()
        {
            InitializeComponent();
        }

        private PyObject ToPyObj(dynamic val)
        {
            if (val is PyObject pyObj) return pyObj;
            if (val == null) return new PyString("");

            if (val is string strVal) return new PyString(strVal);
            if (val is bool boolVal) return PyObject.FromManagedObject(boolVal);
            if (val is int intVal) return new PyInt(intVal);
            if (val is long longVal) return new PyInt(longVal);
            if (val is double dblVal) return new PyFloat(dblVal);

            return new PyString(val.ToString() ?? "");
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitPythonEngine();
            RefreshAll();
        }

        private void InitPythonEngine()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string pythonHome = Path.Combine(baseDir, "python_embed");

            Runtime.PythonDLL = Path.Combine(pythonHome, "python311.dll");
            PythonEngine.PythonHome = pythonHome;
            PythonEngine.Initialize();

            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                sys.path.append(baseDir);

                dynamic arduinoModelModule = Py.Import("arduino_model");
                pyModel = arduinoModelModule.ArduinoBlockModel();

                var result = pyModel.load_mods();
                bool success = result[0];
                string msg = result[1]?.ToString() ?? "";

                if (!success)
                {
                    MessageBox.Show(msg, "ファイルが見つかりません", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                CatComboBox.Items.Clear();
                foreach (var cat in pyModel.categories)
                {
                    CatComboBox.Items.Add(cat.ToString());
                }
                CatComboBox.SelectedIndex = 0;
            }
        }

        private SolidColorBrush GetCategoryBrush(string categoryName)
        {
            if (categoryName != null && CategoryColors.TryGetValue(categoryName, out var brush))
                return brush;
            return CategoryColors["その他"];
        }

        private void RefreshAll()
        {
            if (pyModel == null) return;

            using (Py.GIL())
            {
                RenderPalette();
                RenderWorkspace();
                UpdateCode();
            }
        }

        // ==========================================
        // ① パレット描画
        // ==========================================
        private void RenderPalette()
        {
            if (pyModel == null) return;

            PaletteContainer.Children.Clear();
            string selectedCat = CatComboBox.SelectedItem?.ToString() ?? "すべて";

            using (Py.GIL())
            {
                dynamic json = Py.Import("json");

                List<string> displayCats = new List<string>();
                if (selectedCat == "すべて")
                {
                    foreach (var c in pyModel.categories)
                        if (c.ToString() != "すべて") displayCats.Add(c.ToString());
                }
                else
                {
                    displayCats.Add(selectedCat);
                }

                dynamic declaredVars = pyModel.get_declared_variables();

                foreach (var cat in displayCats)
                {
                    List<dynamic> allCatBlocks = new List<dynamic>();
                    foreach (var b in pyModel.block_configs)
                    {
                        if (b.get("category", "その他").ToString() == cat)
                            allCatBlocks.Add(b);
                    }

                    if (cat == "変数・代入" || cat == "変数")
                    {
                        foreach (var vnameObj in declaredVars)
                        {
                            string vname = vnameObj.ToString();

                            dynamic varRefConfig = json.loads($@"{{
                                ""name"": ""変数 {vname}"",
                                ""category"": ""{cat}"",
                                ""is_expression"": true,
                                ""template"": ""{vname}"",
                                ""params"": []
                            }}");
                            allCatBlocks.Add(varRefConfig);

                            dynamic varAssignConfig = json.loads($@"{{
                                ""name"": ""{vname} に代入"",
                                ""category"": ""{cat}"",
                                ""is_expression"": false,
                                ""template"": ""{vname} = {{{{val}}}}; "",
                                ""params"": [{{""key"": ""val"", ""label"": ""="", ""type"": ""slot"", ""default"": ""0""}}]
                            }}");
                            allCatBlocks.Add(varAssignConfig);
                        }
                    }

                    if (allCatBlocks.Count == 0) continue;

                    TextBlock header = new TextBlock
                    {
                        Text = $"■ {cat}",
                        FontWeight = FontWeights.Bold,
                        Foreground = GetCategoryBrush(cat),
                        Margin = new Thickness(2, 8, 2, 2)
                    };
                    PaletteContainer.Children.Add(header);

                    foreach (var config in allCatBlocks)
                    {
                        Border blockUI = CreatePaletteBlockUI(config, cat);
                        PaletteContainer.Children.Add(blockUI);
                    }
                }
            }
        }

        private Border CreatePaletteBlockUI(dynamic config, string category)
        {
            string name = config["name"].ToString();
            bool isExpr = false;
            using (Py.GIL())
            {
                isExpr = ((PyObject)config.get("is_expression", false)).IsTrue();
            }

            Border border = new Border
            {
                Background = GetCategoryBrush(category),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#44FFFFFF")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(2),
                Padding = new Thickness(5),
                Cursor = Cursors.Hand
            };

            TextBlock txt = new TextBlock
            {
                Text = $"{(isExpr ? "◆" : "+")} [{category}] {name}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold
            };
            border.Child = txt;

            border.MouseLeftButtonDown += (s, e) =>
            {
                isPaletteDrag = true;
                draggingConfig = config;
                draggingBlockGroup.Clear();
                using (Py.GIL())
                {
                    if (pyModel != null)
                    {
                        draggingBlockGroup.Add(pyModel.create_default_block_data(config));
                    }
                }
                StartCustomDrag(CreateBlockGroupPreviewUI(draggingBlockGroup), e);
            };

            return border;
        }

        // ==========================================
        // ② ワークスペース描画
        // ==========================================
        private void RenderWorkspace()
        {
            if (pyModel == null) return;

            SetupContainer.Children.Clear();
            LoopContainer.Children.Clear();
            FuncContainer.Children.Clear();
            activeDropTargets.Clear();

            using (Py.GIL())
            {
                RenderSection(SetupContainer, pyModel.setup_blocks, "setup");
                RenderSection(LoopContainer, pyModel.loop_blocks, "loop");
                RenderSection(FuncContainer, pyModel.func_blocks, "func");
            }
        }

        private void RenderSection(StackPanel container, dynamic blockList, string section)
        {
            activeDropTargets.Add(new DropTarget
            {
                Type = "line",
                TargetList = blockList,
                ContainerPanel = container
            });

            int idx = 0;
            int indentLevel = 0;

            foreach (var block in blockList)
            {
                int currentIndex = idx;
                string template = "";
                string name = "";

                using (Py.GIL())
                {
                    dynamic config = block["config"];
                    template = config.get("template", "").ToString().Trim();
                    name = config.get("name", "").ToString();
                }

                if (template.StartsWith("}") || name.Contains("終わり") || name.Contains("そうでない"))
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                }

                Border blockUI = CreateWorkspaceBlockUI(block, section, currentIndex, isNested: false, indentLevel: indentLevel);
                container.Children.Add(blockUI);

                if (template.EndsWith("{"))
                {
                    indentLevel++;
                }

                idx++;
            }
        }

        private Border CreateWorkspaceBlockUI(dynamic block, string section, int idx, bool isNested = false, dynamic slotRef = null, string slotKey = null, dynamic defaultVal = null, int indentLevel = 0)
        {
            dynamic config = block["config"];
            dynamic values = block["values"];

            string category = "その他";
            string name = "";
            bool isVarDef = false;

            using (Py.GIL())
            {
                category = config.get("category", "その他").ToString();
                name = config.get("name", "").ToString();
                isVarDef = ((PyObject)config.get("is_var_def", false)).IsTrue();
            }

            SolidColorBrush catBrush = GetCategoryBrush(category);

            Border border = new Border
            {
                Background = catBrush,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FFFFFF")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(isNested ? 2 : (indentLevel * 20 + 2), 2, 2, 2),
                Padding = new Thickness(isNested ? 2 : 4)
            };

            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };

            TextBlock grip = new TextBlock
            {
                Text = " ⠿ ",
                Foreground = Brushes.White,
                Cursor = Cursors.SizeAll,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = isNested ? 10 : 12
            };

            grip.MouseLeftButtonDown += (s, e) =>
            {
                isPaletteDrag = false;
                e.Handled = true;
                draggingBlockGroup.Clear();

                if (isNested)
                {
                    sourceSlotRef = slotRef;
                    sourceSlotKey = slotKey;
                    sourceSlotDefault = defaultVal;
                    draggingBlockGroup.Add(block);

                    using (Py.GIL())
                    {
                        if (slotRef != null && slotKey != null)
                        {
                            slotRef[slotKey] = ToPyObj(defaultVal);
                        }
                    }
                }
                else
                {
                    sourceSection = section;
                    sourceIndex = idx;
                    sourceSlotRef = null;

                    using (Py.GIL())
                    {
                        if (pyModel != null)
                        {
                            dynamic srcList = pyModel.GetAttr($"{sourceSection}_blocks");
                            int count = (int)srcList.__len__();
                            for (int i = sourceIndex; i < count; i++)
                            {
                                draggingBlockGroup.Add(srcList[i]);
                            }
                        }
                    }
                }

                StartCustomDrag(CreateBlockGroupPreviewUI(draggingBlockGroup), e);
            };
            sp.Children.Add(grip);

            TextBlock title = new TextBlock
            {
                Text = name,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 0),
                FontSize = isNested ? 10 : 12
            };
            sp.Children.Add(title);

            bool hasParams = false;
            using (Py.GIL()) { hasParams = ((PyObject)config.get("params", false)).IsTrue(); }

            if (hasParams)
            {
                foreach (var p in config["params"])
                {
                    string key = p["key"].ToString();
                    string pType = p["type"].ToString();
                    string label = "";
                    dynamic val = "";

                    using (Py.GIL())
                    {
                        label = p.get("label", "").ToString();
                        val = values.get(key, p.get("default", ""));
                    }

                    if (!string.IsNullOrEmpty(label))
                    {
                        sp.Children.Add(new TextBlock
                        {
                            Text = label,
                            Foreground = Brushes.White,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(2, 0, 2, 0)
                        });
                    }

                    if (pType == "input")
                    {
                        TextBox tb = new TextBox
                        {
                            Text = val.ToString(),
                            Width = Math.Max(35, val.ToString().Length * 9 + 15),
                            Margin = new Thickness(2, 0, 2, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = Brushes.White,
                            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E0")),
                            BorderThickness = new Thickness(1)
                        };
                        string currentKey = key;
                        tb.TextChanged += (s, e) =>
                        {
                            tb.Width = Math.Max(35, tb.Text.Length * 9 + 15);
                            using (Py.GIL())
                            {
                                values[currentKey] = ToPyObj(tb.Text);
                                if (pyModel != null) pyModel.save_state();
                                if (isVarDef && currentKey == "name") RenderPalette();
                            }
                            UpdateCode();
                        };
                        sp.Children.Add(tb);
                    }
                    else if (pType == "toggle")
                    {
                        ComboBox cb = new ComboBox { Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                        foreach (var opt in p["options"]) cb.Items.Add(opt.ToString());
                        cb.SelectedItem = val.ToString();

                        string currentKey = key;
                        cb.SelectionChanged += (s, e) =>
                        {
                            if (cb.SelectedItem == null) return;
                            using (Py.GIL())
                            {
                                values[currentKey] = ToPyObj(cb.SelectedItem.ToString());
                                if (pyModel != null) pyModel.save_state();
                            }
                            UpdateCode();
                        };
                        sp.Children.Add(cb);
                    }
                    else if (pType == "slot")
                    {
                        Border slotBorder = new Border
                        {
                            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF8FF")),
                            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3182CE")),
                            BorderThickness = new Thickness(1.5),
                            CornerRadius = new CornerRadius(3),
                            Padding = new Thickness(2),
                            Margin = new Thickness(2, 0, 2, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        activeDropTargets.Add(new DropTarget { Type = "slot", SlotRef = values, SlotKey = key, Element = slotBorder });

                        bool isChildBlock = false;
                        using (Py.GIL())
                        {
                            dynamic builtins = Py.Import("builtins");
                            isChildBlock = (builtins.type(val).__name__.ToString() == "dict");
                        }

                        if (isChildBlock)
                        {
                            StackPanel slotContent = new StackPanel { Orientation = Orientation.Horizontal };
                            dynamic defVal = "";
                            using (Py.GIL()) { defVal = p.get("default", ""); }

                            Border childBlockUI = CreateWorkspaceBlockUI(val, section, idx, isNested: true, slotRef: values, slotKey: key, defaultVal: defVal);
                            slotContent.Children.Add(childBlockUI);

                            Button clearBtn = new Button { Content = "✕", Background = Brushes.DarkRed, Foreground = Brushes.White, FontSize = 10, Width = 16, Height = 16, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                            string currentKey = key;
                            clearBtn.Click += (s, e) =>
                            {
                                using (Py.GIL())
                                {
                                    values[currentKey] = ToPyObj(defVal);
                                    if (pyModel != null) pyModel.save_state();
                                }
                                RefreshAll();
                            };
                            slotContent.Children.Add(clearBtn);
                            slotBorder.Child = slotContent;
                        }
                        else
                        {
                            TextBox slotTb = new TextBox
                            {
                                Text = val.ToString(),
                                Width = Math.Max(30, val.ToString().Length * 8 + 10),
                                VerticalAlignment = VerticalAlignment.Center,
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0)
                            };
                            string currentKey = key;
                            slotTb.TextChanged += (s, e) =>
                            {
                                slotTb.Width = Math.Max(30, slotTb.Text.Length * 8 + 10);
                                using (Py.GIL())
                                {
                                    values[currentKey] = ToPyObj(slotTb.Text);
                                    if (pyModel != null) pyModel.save_state();
                                }
                                UpdateCode();
                            };
                            slotBorder.Child = slotTb;
                        }
                        sp.Children.Add(slotBorder);
                    }
                }
            }

            if (!isNested)
            {
                Button delBtn = new Button
                {
                    Content = "✕",
                    Background = Brushes.DarkRed,
                    Foreground = Brushes.White,
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(5, 0, 0, 0),
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                delBtn.Click += (s, e) =>
                {
                    using (Py.GIL())
                    {
                        if (pyModel != null)
                        {
                            dynamic targetList = pyModel.GetAttr($"{section}_blocks");
                            targetList.pop(idx);
                            pyModel.save_state();
                        }
                    }
                    RefreshAll();
                };

                DockPanel dock = new DockPanel();
                DockPanel.SetDock(delBtn, Dock.Right);
                dock.Children.Add(delBtn);
                dock.Children.Add(sp);
                border.Child = dock;
            }
            else
            {
                border.Child = sp;
            }

            return border;
        }

        private UIElement CreateBlockGroupPreviewUI(List<dynamic> blocks)
        {
            StackPanel groupPanel = new StackPanel { Orientation = Orientation.Vertical, Opacity = 0.85 };

            using (Py.GIL())
            {
                foreach (var b in blocks)
                {
                    dynamic config = b["config"];
                    string name = config.get("name", "").ToString();
                    string category = config.get("category", "その他").ToString();

                    Border bUI = new Border
                    {
                        Background = GetCategoryBrush(category),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#66FFFFFF")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(1),
                        Padding = new Thickness(6, 3, 6, 3),
                        Child = new TextBlock { Text = name, Foreground = Brushes.White, FontWeight = FontWeights.Bold }
                    };
                    groupPanel.Children.Add(bUI);
                }
            }
            return groupPanel;
        }

        // ==========================================
        // ③ ドラッグ＆ドロップ リアルタイム判定＆ハイライト
        // ==========================================
        private void StartCustomDrag(UIElement element, MouseButtonEventArgs e)
        {
            draggingElement = element;
            DragCanvas.Children.Clear();
            DragCanvas.Children.Add(draggingElement);

            Point pt = e.GetPosition(DragCanvas);
            Canvas.SetLeft(draggingElement, pt.X - 10);
            Canvas.SetTop(draggingElement, pt.Y - 10);

            Mouse.Capture(this, CaptureMode.SubTree);
            this.MouseMove += Window_MouseMove;
            this.MouseLeftButtonUp += Window_MouseLeftButtonUp;
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingElement != null)
            {
                Point pt = e.GetPosition(DragCanvas);
                Canvas.SetLeft(draggingElement, pt.X - 10);
                Canvas.SetTop(draggingElement, pt.Y - 10);

                Point mousePosWindow = e.GetPosition(this);
                Point trashPt = e.GetPosition(TrashBorder);

                bool isOverTrash = (trashPt.X >= 0 && trashPt.X <= TrashBorder.ActualWidth &&
                                   trashPt.Y >= 0 && trashPt.Y <= TrashBorder.ActualHeight);

                if (isOverTrash)
                {
                    TrashBorder.Background = Brushes.DarkRed;
                    TrashText.Text = " 🗑️ 離して削除 ";
                    ClearHoverHighlight();
                }
                else
                {
                    TrashBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
                    TrashText.Text = " 🗑️ ここへドロップで削除 ";
                    UpdateHoverHighlight(mousePosWindow);
                }
            }
        }

        private void UpdateHoverHighlight(Point mousePosWindow)
        {
            DropTarget currentTarget = ResolveDropTarget(mousePosWindow);

            if (currentTarget != lastHoverTarget)
            {
                ClearHoverHighlight();
                lastHoverTarget = currentTarget;
            }

            if (currentTarget != null)
            {
                if (currentTarget.Type == "slot")
                {
                    if (currentTarget.Element is Border b)
                    {
                        b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C6F6D5"));
                        b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#38A169"));
                        b.BorderThickness = new Thickness(2);
                    }
                }
                else if (currentTarget.Type == "line")
                {
                    var container = currentTarget.ContainerPanel;
                    if (container != null)
                    {
                        int idx = currentTarget.CalculatedIndex;

                        if (container.Children.Contains(insertionIndicator))
                        {
                            int currentIndicatorIdx = container.Children.IndexOf(insertionIndicator);
                            if (currentIndicatorIdx != idx)
                            {
                                container.Children.Remove(insertionIndicator);
                                if (idx > currentIndicatorIdx) idx--;
                                idx = Math.Max(0, Math.Min(idx, container.Children.Count));
                                container.Children.Insert(idx, insertionIndicator);
                            }
                        }
                        else
                        {
                            idx = Math.Max(0, Math.Min(idx, container.Children.Count));
                            container.Children.Insert(idx, insertionIndicator);
                        }
                    }
                }
            }
        }

        private void ClearHoverHighlight()
        {
            if (lastHoverTarget != null)
            {
                if (lastHoverTarget.Type == "slot" && lastHoverTarget.Element is Border b)
                {
                    b.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF8FF"));
                    b.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3182CE"));
                    b.BorderThickness = new Thickness(1.5);
                }
                else if (lastHoverTarget.Type == "line" && lastHoverTarget.ContainerPanel != null)
                {
                    if (lastHoverTarget.ContainerPanel.Children.Contains(insertionIndicator))
                    {
                        lastHoverTarget.ContainerPanel.Children.Remove(insertionIndicator);
                    }
                }
                lastHoverTarget = null;
            }

            if (SetupContainer.Children.Contains(insertionIndicator)) SetupContainer.Children.Remove(insertionIndicator);
            if (LoopContainer.Children.Contains(insertionIndicator)) LoopContainer.Children.Remove(insertionIndicator);
            if (FuncContainer.Children.Contains(insertionIndicator)) FuncContainer.Children.Remove(insertionIndicator);
        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingElement == null) return;

            Mouse.Capture(null, CaptureMode.None);
            this.MouseMove -= Window_MouseMove;
            this.MouseLeftButtonUp -= Window_MouseLeftButtonUp;

            Point mousePosWindow = e.GetPosition(this);
            Point trashPt = e.GetPosition(TrashBorder);

            bool isOverTrash = (trashPt.X >= 0 && trashPt.X <= TrashBorder.ActualWidth &&
                               trashPt.Y >= 0 && trashPt.Y <= TrashBorder.ActualHeight);

            ClearHoverHighlight();

            using (Py.GIL())
            {
                if (isOverTrash)
                {
                    if (!isPaletteDrag && sourceSection != null && sourceIndex >= 0 && pyModel != null)
                    {
                        dynamic srcList = pyModel.GetAttr($"{sourceSection}_blocks");
                        int removeCount = draggingBlockGroup.Count;
                        for (int i = 0; i < removeCount; i++)
                        {
                            srcList.pop(sourceIndex);
                        }
                        pyModel.save_state();
                    }
                }
                else
                {
                    DropTarget target = ResolveDropTarget(mousePosWindow);
                    if (target != null)
                    {
                        if (!isPaletteDrag && sourceSection != null && sourceIndex >= 0 && pyModel != null)
                        {
                            dynamic srcList = pyModel.GetAttr($"{sourceSection}_blocks");
                            bool isSameSection = (target.Type == "line" && target.TargetList == srcList);
                            int targetIdx = target.CalculatedIndex;
                            int removeCount = draggingBlockGroup.Count;

                            for (int i = 0; i < removeCount; i++)
                            {
                                srcList.pop(sourceIndex);
                            }

                            if (isSameSection && targetIdx > sourceIndex)
                            {
                                targetIdx -= removeCount;
                                targetIdx = Math.Max(0, targetIdx);
                            }
                            target.CalculatedIndex = targetIdx;
                        }

                        if (target.Type == "slot" && target.SlotRef != null && target.SlotKey != null)
                        {
                            if (draggingBlockGroup.Count > 0)
                            {
                                target.SlotRef[target.SlotKey] = ToPyObj(draggingBlockGroup[0]);
                            }
                        }
                        else if (target.Type == "line" && target.TargetList != null)
                        {
                            dynamic destList = target.TargetList;
                            int insertIdx = Math.Max(0, Math.Min(target.CalculatedIndex, (int)destList.__len__()));

                            for (int i = 0; i < draggingBlockGroup.Count; i++)
                            {
                                destList.insert(insertIdx + i, ToPyObj(draggingBlockGroup[i]));
                            }
                        }

                        if (pyModel != null) pyModel.save_state();
                    }
                    else
                    {
                        if (sourceSlotRef != null && sourceSlotKey != null && draggingBlockGroup.Count > 0)
                        {
                            sourceSlotRef[sourceSlotKey] = ToPyObj(draggingBlockGroup[0]);
                        }
                    }
                }
            }

            DragCanvas.Children.Clear();
            draggingElement = null;
            draggingBlockGroup.Clear();
            sourceSection = null;
            sourceIndex = -1;
            sourceSlotRef = null;
            sourceSlotKey = null;

            TrashBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            TrashText.Text = " 🗑️ ここへドロップで削除 ";

            RefreshAll();
        }

        private DropTarget ResolveDropTarget(Point mousePosWindow)
        {
            for (int i = activeDropTargets.Count - 1; i >= 0; i--)
            {
                var target = activeDropTargets[i];
                if (target.Type == "slot" && target.Element != null)
                {
                    Point pt = target.Element.TransformToAncestor(this).Transform(new Point(0, 0));
                    Rect rect = new Rect(pt, new Size(target.Element.ActualWidth, target.Element.ActualHeight));
                    if (rect.Contains(mousePosWindow)) return target;
                }
            }

            for (int i = activeDropTargets.Count - 1; i >= 0; i--)
            {
                var target = activeDropTargets[i];
                if (target.Type == "line" && target.ContainerPanel != null)
                {
                    Point pt = target.ContainerPanel.TransformToAncestor(this).Transform(new Point(0, 0));
                    Rect rect = new Rect(pt, new Size(target.ContainerPanel.ActualWidth, Math.Max(25, target.ContainerPanel.ActualHeight)));

                    if (rect.Contains(mousePosWindow))
                    {
                        int insertIndex = 0;
                        foreach (UIElement child in target.ContainerPanel.Children)
                        {
                            if (child == insertionIndicator) continue;

                            Point childPt = child.TransformToAncestor(this).Transform(new Point(0, 0));
                            double midY = childPt.Y + (child.RenderSize.Height / 2.0);

                            if (mousePosWindow.Y < midY)
                            {
                                target.CalculatedIndex = insertIndex;
                                return target;
                            }
                            insertIndex++;
                        }
                        target.CalculatedIndex = insertIndex;
                        return target;
                    }
                }
            }

            return null;
        }

        // ==========================================
        // ④ コード自動生成・イベントハンドラ
        // ==========================================
        private void UpdateCode()
        {
            if (pyModel == null) return;

            using (Py.GIL())
            {
                CodeTextBox.Text = pyModel.generate_arduino_code().ToString();
            }
        }

        private void CatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderPalette();
        private void Undo_Click(object sender, RoutedEventArgs e) { using (Py.GIL()) { if (pyModel != null && (bool)pyModel.undo()) RefreshAll(); } }
        private void Redo_Click(object sender, RoutedEventArgs e) { using (Py.GIL()) { if (pyModel != null && (bool)pyModel.redo()) RefreshAll(); } }
        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "現在の編集内容は消去されます。\nよろしいですか？",
                "作成・消去の確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result == MessageBoxResult.Yes)
            {
                using (Py.GIL())
                {
                    if (pyModel != null)
                    {
                        pyModel.clear_all();
                        pyModel.save_state();
                        RefreshAll();
                    }
                }
            }
        }

        private void NewFile_Click(object sender, RoutedEventArgs e) => ClearAll_Click(sender, e);
        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json" };
            if (dlg.ShowDialog() == true && pyModel != null)
            {
                MessageBoxResult result = MessageBox.Show(
                    "ファイルを開くと、現在ワークスペースにある編集内容は破棄されます。\n読み込んでもよろしいですか？",
                    "ファイル読み込みの確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    using (Py.GIL())
                    {
                        pyModel.load_project_file(dlg.FileName);
                        pyModel.save_state();
                        RefreshAll();
                    }
                }
            }
        }
        private void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog { Filter = "JSON Files (*.json)|*.json" };
            if (dlg.ShowDialog() == true && pyModel != null)
            {
                using (Py.GIL()) { pyModel.save_project_file(dlg.FileName); }
            }
        }
        private void SaveFileAs_Click(object sender, RoutedEventArgs e) => SaveFile_Click(sender, e);

        // メニューの「終了」が押された時
        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        // ウィンドウが閉じられる直前の確認ダイアログ処理
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "アプリケーションを終了しますか？\n（保存していない変更内容は失われます）",
                "終了の確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true; // 終了をキャンセル
            }
        }

        // ウィンドウが閉じた後のプロセス完全停止処理
        private void Window_Closed(object sender, EventArgs e)
        {
            try
            {
                if (PythonEngine.IsInitialized)
                {
                    PythonEngine.Shutdown();
                }
            }
            catch
            {
                // 終了時のシャットダウンエラーは無視
            }
            finally
            {
                Environment.Exit(0); // バックグラウンドプロセスを完全に終了
            }
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(CodeTextBox.Text);

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.Z) Undo_Click(sender, e);
                if (e.Key == Key.Y) Redo_Click(sender, e);
                if (e.Key == Key.S) SaveFile_Click(sender, e);
                if (e.Key == Key.O) OpenFile_Click(sender, e);
                if (e.Key == Key.N) NewFile_Click(sender, e);
            }
        }
    }
}