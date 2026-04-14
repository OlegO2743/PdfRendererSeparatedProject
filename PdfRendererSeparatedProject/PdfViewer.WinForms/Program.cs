using System.Drawing;
using System.Windows.Forms;
using PdfCore.Color;

namespace PdfViewer.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
