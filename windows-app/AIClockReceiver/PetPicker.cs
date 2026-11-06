using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AIClockReceiver
{
    internal sealed class PetPicker
    {
        public string Slug;
        public PetAnimation Animation;
        private List<CatalogPet> pets = new List<CatalogPet>();
        private CancellationTokenSource load;
        private bool closed;

        public bool Show(Window owner, PetLibrary library, string provider, string current, PetAnimation builtin)
        {
            var dialog = Receiver.LoadWindow("PetPicker.xaml");
            dialog.Owner = owner;
            ((TextBlock)dialog.FindName("PetTitle")).Text = provider + " 的桌宠";
            var search = (TextBox)dialog.FindName("Search");
            var list = (ListBox)dialog.FindName("List");
            var image = (Image)dialog.FindName("Preview");
            var action = (ComboBox)dialog.FindName("Action");
            var info = (TextBlock)dialog.FindName("Info");
            var name = (TextBlock)dialog.FindName("PetName");
            var choose = (Button)dialog.FindName("Choose");
            var clock = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            var lifetime = new CancellationTokenSource();
            Action draw = delegate {
                image.Source = Animation == null ? null : Animation.Frame(action.SelectedIndex == 2,
                    action.SelectedIndex == 1, SystemParameters.ClientAreaAnimation ? clock.Elapsed.TotalSeconds : 0);
            };
            timer.Tick += delegate { draw(); };
            string catalogStatus = "正在读取 Petdex 目录…";
            Action filter = delegate {
                string query = search.Text.Trim();
                var matches = pets.Where(p => (p.Name + " " + p.Slug).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                list.ItemsSource = matches;
                info.Text = matches.Count == 0 && pets.Count > 0 ? "没有找到匹配的宠物" : catalogStatus;
            };
            list.SelectionChanged += async delegate {
                if (load != null) { load.Cancel(); load = null; }
                var item = list.SelectedItem as CatalogPet;
                choose.IsEnabled = false; Animation = null; draw();
                name.Text = item == null ? "" : item.Name;
                if (item == null) return;
                info.Text = "正在载入动画…";
                using (var currentLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    load = currentLoad;
                    try {
                        var animation = await library.Get(item, currentLoad.Token);
                        if (closed || load != currentLoad) return;
                        Slug = item.Slug; Animation = animation; draw();
                        choose.IsEnabled = true; info.Text = catalogStatus;
                    }
                    catch (Exception e) { if (!closed && load == currentLoad) info.Text = e.GetBaseException().Message; }
                    finally { if (load == currentLoad) load = null; }
                }
            };
            search.TextChanged += delegate { filter(); };
            choose.Click += delegate { dialog.DialogResult = true; };
            ((Button)dialog.FindName("DefaultPet")).Click += delegate { Slug = null; Animation = builtin; dialog.DialogResult = true; };
            dialog.Loaded += async delegate {
                timer.Start(); info.Text = catalogStatus;
                try {
                    var result = await library.Catalog(lifetime.Token);
                    if (closed) return;
                    pets = result;
                    catalogStatus = library.Offline ? "离线目录，可使用已下载的宠物" : "选择宠物可预览动作";
                    filter();
                    list.SelectedItem = pets.FirstOrDefault(p => p.Slug == current);
                    search.Focus();
                }
                catch (Exception e) {
                    if (!closed) { catalogStatus = "目录读取失败：" + e.GetBaseException().Message; info.Text = catalogStatus; }
                }
            };
            dialog.Closed += delegate { closed = true; timer.Stop(); lifetime.Cancel(); };
            try { return dialog.ShowDialog() == true; }
            finally { lifetime.Dispose(); }
        }
    }
}
