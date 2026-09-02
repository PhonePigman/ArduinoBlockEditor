#nullable disable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ArduinoBlockEditor
{
    public partial class MainWindow : Window
    {
        private ArduinoBlockModel blockModel;
        private UIElement draggingElement = null;
        private JsonNode draggingConfig = null;
        private readonly List<JsonNode> draggingBlockGroup = new();
        private bool isPaletteDrag = false;

        // ドラッグ元情報
        private string sourceSection = null;
        private int sourceIndex = -1;
        private JsonObject sourceSlotRef = null;
        private string sourceSlotKey = null;
        private string sourceSlotDefault = null;

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
            public List<JsonNode> TargetList { get; set; } = null;
            public StackPanel ContainerPanel { get; set; } = null;
            public int CalculatedIndex { get; set; }

            public JsonObject SlotRef { get; set; } = null;
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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InitModel();
            RefreshAll();
        }

        private void InitModel()
        {
            blockModel = new ArduinoBlockModel();
            var (success, msg) = blockModel.LoadMods();

            if (!success)
            {
                MessageBox.Show(msg, "ファイルが見つかりません", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            CatComboBox.Items.Clear();
            foreach (var cat in blockModel.Categories)
            {
                CatComboBox.Items.Add(cat);
            }
            if (CatComboBox.Items.Count > 0)
            {
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
            if (blockModel == null) return;

            RenderPalette();
            RenderWorkspace();
            UpdateCode();
        }

        // ==========================================
        // ① パレット描画
        // ==========================================
        private void RenderPalette()
        {
            if (blockModel == null) return;

            PaletteContainer.Children.Clear();
            string selectedCat = CatComboBox.SelectedItem?.ToString() ?? "すべて";

            List<string> displayCats = new List<string>();
            if (selectedCat == "すべて")
            {
                foreach (var c in blockModel.Categories)
                    if (c != "すべて") displayCats.Add(c);
            }
            else
            {
                displayCats.Add(selectedCat);
            }

            List<string> declaredVars = blockModel.GetDeclaredVariables();

            foreach (var cat in displayCats)
            {
                List<JsonNode> allCatBlocks = new List<JsonNode>();
                foreach (var b in blockModel.BlockConfigs)
                {
                    if ((b["category"]?.ToString() ?? "その他") == cat)
                        allCatBlocks.Add(b);
                }

                if (cat == "変数・代入" || cat == "変数")
                {
                    foreach (var vname in declaredVars)
                    {
                        var varRefConfig = JsonNode.Parse($@"{{
                            ""name"": ""変数 {vname}"",
                            ""category"": ""{cat}"",
                            ""is_expression"": true,
                            ""template"": ""{vname}"",
                            ""params"": []
                        }}");
                        allCatBlocks.Add(varRefConfig);

                        var varAssignConfig = JsonNode.Parse($@"{{
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

        private Border CreatePaletteBlockUI(JsonNode config, string category)
        {
            string name = config["name"]?.ToString() ?? "";
            bool isExpr = config["is_expression"]?.GetValue<bool>() ?? false;

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

                if (blockModel != null)
                {
                    draggingBlockGroup.Add(blockModel.CreateDefaultBlockData(config));
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
            if (blockModel == null) return;

            SetupContainer.Children.Clear();
            LoopContainer.Children.Clear();
            FuncContainer.Children.Clear();
            activeDropTargets.Clear();

            RenderSection(SetupContainer, blockModel.SetupBlocks, "setup");
            RenderSection(LoopContainer, blockModel.LoopBlocks, "loop");
            RenderSection(FuncContainer, blockModel.FuncBlocks, "func");
        }

        private void RenderSection(StackPanel container, List<JsonNode> blockList, string section)
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
                JsonNode config = block["config"];
                string template = config?["template"]?.ToString().Trim() ?? "";
                string name = config?["name"]?.ToString() ?? "";

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

        private Border CreateWorkspaceBlockUI(JsonNode block, string section, int idx, bool isNested = false, JsonObject slotRef = null, string slotKey = null, string defaultVal = null, int indentLevel = 0)
        {
            JsonNode config = block["config"];
            JsonObject values = block["values"] as JsonObject ?? new JsonObject();

            string category = config?["category"]?.ToString() ?? "その他";
            string name = config?["name"]?.ToString() ?? "";
            bool isVarDef = config?["is_var_def"]?.GetValue<bool>() ?? false;

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

                    if (slotRef != null && slotKey != null)
                    {
                        slotRef[slotKey] = defaultVal ?? "";
                    }
                }
                else
                {
                    sourceSection = section;
                    sourceIndex = idx;
                    sourceSlotRef = null;

                    if (blockModel != null)
                    {
                        List<JsonNode> srcList = GetSectionList(sourceSection);
                        for (int i = sourceIndex; i < srcList.Count; i++)
                        {
                            draggingBlockGroup.Add(srcList[i]);
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

            if (config?["params"] is JsonArray paramsArr && paramsArr.Count > 0)
            {
                foreach (var p in paramsArr)
                {
                    string key = p["key"]?.ToString();
                    string pType = p["type"]?.ToString();
                    string label = p["label"]?.ToString() ?? "";
                    JsonNode valNode = values[key] ?? p["default"]?.ToString() ?? "";

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
                        string valStr = valNode.ToString();
                        TextBox tb = new TextBox
                        {
                            Text = valStr,
                            Width = Math.Max(35, valStr.Length * 9 + 15),
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
                            values[currentKey] = tb.Text;
                            if (blockModel != null) blockModel.SaveState();
                            if (isVarDef && currentKey == "name") RenderPalette();
                            UpdateCode();
                        };
                        sp.Children.Add(tb);
                    }
                    else if (pType == "toggle")
                    {
                        ComboBox cb = new ComboBox { Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                        if (p["options"] is JsonArray optionsArr)
                        {
                            foreach (var opt in optionsArr) cb.Items.Add(opt.ToString());
                        }
                        cb.SelectedItem = valNode.ToString();

                        string currentKey = key;
                        cb.SelectionChanged += (s, e) =>
                        {
                            if (cb.SelectedItem == null) return;
                            values[currentKey] = cb.SelectedItem.ToString();
                            if (blockModel != null) blockModel.SaveState();
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

                        bool isChildBlock = (valNode is JsonObject);

                        if (isChildBlock)
                        {
                            StackPanel slotContent = new StackPanel { Orientation = Orientation.Horizontal };
                            string defVal = p["default"]?.ToString() ?? "";

                            Border childBlockUI = CreateWorkspaceBlockUI(valNode, section, idx, isNested: true, slotRef: values, slotKey: key, defaultVal: defVal);
                            slotContent.Children.Add(childBlockUI);

                            Button clearBtn = new Button { Content = "✕", Background = Brushes.DarkRed, Foreground = Brushes.White, FontSize = 10, Width = 16, Height = 16, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                            string currentKey = key;
                            clearBtn.Click += (s, e) =>
                            {
                                values[currentKey] = defVal;
                                if (blockModel != null) blockModel.SaveState();
                                RefreshAll();
                            };
                            slotContent.Children.Add(clearBtn);
                            slotBorder.Child = slotContent;
                        }
                        else
                        {
                            string valStr = valNode?.ToString() ?? "";
                            TextBox slotTb = new TextBox
                            {
                                Text = valStr,
                                Width = Math.Max(30, valStr.Length * 8 + 10),
                                VerticalAlignment = VerticalAlignment.Center,
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0)
                            };
                            string currentKey = key;
                            slotTb.TextChanged += (s, e) =>
                            {
                                slotTb.Width = Math.Max(30, slotTb.Text.Length * 8 + 10);
                                values[currentKey] = slotTb.Text;
                                if (blockModel != null) blockModel.SaveState();
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
                    if (blockModel != null)
                    {
                        List<JsonNode> targetList = GetSectionList(section);
                        targetList.RemoveAt(idx);
                        blockModel.SaveState();
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

        private List<JsonNode> GetSectionList(string section)
        {
            return section switch
            {
                "setup" => blockModel.SetupBlocks,
                "loop" => blockModel.LoopBlocks,
                "func" => blockModel.FuncBlocks,
                _ => null
            };
        }

        private UIElement CreateBlockGroupPreviewUI(List<JsonNode> blocks)
        {
            StackPanel groupPanel = new StackPanel { Orientation = Orientation.Vertical, Opacity = 0.85 };

            foreach (var b in blocks)
            {
                JsonNode config = b["config"];
                string name = config?["name"]?.ToString() ?? "";
                string category = config?["category"]?.ToString() ?? "その他";

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

            if (isOverTrash)
            {
                if (!isPaletteDrag && sourceSection != null && sourceIndex >= 0 && blockModel != null)
                {
                    List<JsonNode> srcList = GetSectionList(sourceSection);
                    int removeCount = draggingBlockGroup.Count;
                    for (int i = 0; i < removeCount; i++)
                    {
                        if (sourceIndex < srcList.Count)
                        {
                            srcList.RemoveAt(sourceIndex);
                        }
                    }
                    blockModel.SaveState();
                }
            }
            else
            {
                DropTarget target = ResolveDropTarget(mousePosWindow);
                if (target != null)
                {
                    if (!isPaletteDrag && sourceSection != null && sourceIndex >= 0 && blockModel != null)
                    {
                        List<JsonNode> srcList = GetSectionList(sourceSection);
                        bool isSameSection = (target.Type == "line" && target.TargetList == srcList);
                        int targetIdx = target.CalculatedIndex;
                        int removeCount = draggingBlockGroup.Count;

                        for (int i = 0; i < removeCount; i++)
                        {
                            if (sourceIndex < srcList.Count)
                            {
                                srcList.RemoveAt(sourceIndex);
                            }
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
                            target.SlotRef[target.SlotKey] = draggingBlockGroup[0].DeepClone();
                        }
                    }
                    else if (target.Type == "line" && target.TargetList != null)
                    {
                        List<JsonNode> destList = target.TargetList;
                        int insertIdx = Math.Max(0, Math.Min(target.CalculatedIndex, destList.Count));

                        for (int i = 0; i < draggingBlockGroup.Count; i++)
                        {
                            destList.Insert(insertIdx + i, draggingBlockGroup[i].DeepClone());
                        }
                    }

                    if (blockModel != null) blockModel.SaveState();
                }
                else
                {
                    if (sourceSlotRef != null && sourceSlotKey != null && draggingBlockGroup.Count > 0)
                    {
                        sourceSlotRef[sourceSlotKey] = draggingBlockGroup[0].DeepClone();
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
            if (blockModel == null) return;
            CodeTextBox.Text = blockModel.GenerateArduinoCode();
        }

        private void CatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderPalette();
        private void Undo_Click(object sender, RoutedEventArgs e) { if (blockModel != null && blockModel.Undo()) RefreshAll(); }
        private void Redo_Click(object sender, RoutedEventArgs e) { if (blockModel != null && blockModel.Redo()) RefreshAll(); }
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
                if (blockModel != null)
                {
                    blockModel.ClearAll();
                    blockModel.SaveState();
                    RefreshAll();
                }
            }
        }

        private void NewFile_Click(object sender, RoutedEventArgs e) => ClearAll_Click(sender, e);
        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json" };
            if (dlg.ShowDialog() == true && blockModel != null)
            {
                MessageBoxResult result = MessageBox.Show(
                    "ファイルを開くと、現在ワークスペースにある編集内容は破棄されます。\n読み込んでもよろしいですか？",
                    "ファイル読み込みの確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    blockModel.LoadProjectFile(dlg.FileName);
                    blockModel.SaveState();
                    RefreshAll();
                }
            }
        }
        private void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog { Filter = "JSON Files (*.json)|*.json" };
            if (dlg.ShowDialog() == true && blockModel != null)
            {
                blockModel.SaveProjectFile(dlg.FileName);
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

        // ウィンドウが閉じた後の処理
        private void Window_Closed(object sender, EventArgs e)
        {
            // Python Engine のシャットダウン処理は不要になりました
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