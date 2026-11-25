// Hallow Torrents 1.12 - the window side of "Remove all" (the question, and the button's handler); the engine side is RemoveAll112.cs.
using System;
using System.Windows;
using System.Windows.Controls;

namespace HallowTorrents
{
    public static partial class Dialogs
    {
        // null = cancelled; else { onlyFinished, deleteFiles }
        public static Window RemoveAllWindow(int all, int finished, Action<bool[]> done)
        {
            var w = Make("Hallow Torrents", 480); var sp = Body(w);
            var title = Ui.T("Remove all " + all + (all == 1 ? " torrent" : " torrents") + "?", 15, true, null, 6); sp.Children.Add(title);
            sp.Children.Add(Ui.Muted("They leave the list. The downloaded files stay where they are unless you tick the box (then they go to the Recycle Bin, so you can still get them back).", 12.5, 10));
            CheckBox only = null; Button ok = null;
            only = Ui.Switch("Only the finished ones (" + finished + ") - keep the ones still downloading", false, on => { var n = on ? finished : all; title.Text = (on ? "Remove the " : "Remove all ") + n + (on ? " finished" : "") + (n == 1 ? " torrent" : " torrents") + "?"; ok.IsEnabled = n > 0; });
            only.Margin = new Thickness(0, 0, 0, 6); sp.Children.Add(only);
            var del = Ui.Switch("Also move their files to the Recycle Bin", false, null); sp.Children.Add(del);
            bool[] r = null; var row = Buttons(sp);
            row.Children.Add(Ui.Btn("Cancel", () => w.Close()));
            ok = Ui.Danger("Remove all", () => { r = new[] { only.IsChecked == true, del.IsChecked == true }; w.Close(); }); ok.Margin = new Thickness(0); ok.IsEnabled = all > 0; row.Children.Add(ok);
            w.Loaded += (s, e) => ((Button)row.Children[0]).Focus();
            w.Closed += (s, e) => { if (done != null) done(r); };
            return w;
        }
    }
}

namespace HallowTorrents
{
    public partial class MainWindow
    {
        void AskRemoveAll()
        {
            if (c.SnapMode) return;
            int all, fin; all = c.Core.RemoveAllIds(false).Count; fin = c.Core.RemoveAllIds(true).Count;
            if (all == 0) { Say("Nothing to remove - the list is empty."); return; }
            var w = Dialogs.RemoveAllWindow(all, fin, r =>
            {
                if (r == null) return;
                selected = null;
                c.Core.RemoveAll(r[0], r[1]).ContinueWith(t => Dispatcher.BeginInvoke(new Action(() =>
                {
                    var n = t.Result.Item1; Say(t.Result.Item2 ?? ("Removed " + n + (n == 1 ? " torrent" : " torrents") + (r[1] ? " - their files are in the Recycle Bin." : ". Their files are where they were.")));
                    RefreshTransfers(); BuildDetails();
                })));
            });
            Dialogs.Own(w, this); w.ShowDialog();
        }
    }
}
