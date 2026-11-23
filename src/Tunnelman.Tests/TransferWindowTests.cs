using System.Windows;
using Tunnelman.Tunnels;

namespace Tunnelman.Tests;

public sealed class TransferWindowTests
{
    [Fact]
    public void Closing_drops_the_tunnel_records()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Tunnelman;component/Themes/Dark.xaml", UriKind.Absolute),
                    });
                }

                var record = SessionTestHost.Hop("office", 22001);
                var window = new TransferWindow(
                    export: true,
                    TunnelTransfer.BuildExport([record]),
                    [record],
                    sourcePath: null,
                    import: null);
                window.Close();
                Assert.Equal(0, window.RowCount);
                Assert.Equal(0, window.ExistingCount);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null)
            throw error;
    }
}
