using System;
using System.Windows.Forms;

namespace KerbinMaps
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            ApplicationConfiguration.Initialize();
            Core.Store.MigrateOldFolders();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Error inesperado:\n\n" + e.Exception.Message, "Koogle Kerbin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Run(new UI.MainForm(args));
        }
    }
}
