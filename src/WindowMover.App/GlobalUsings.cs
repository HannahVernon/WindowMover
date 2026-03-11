// Resolve WPF vs WinForms type ambiguities.
// The App project uses WPF for UI and WinForms only for NotifyIcon (system tray).
global using System.IO;
global using Application = System.Windows.Application;
global using Button = System.Windows.Controls.Button;
global using DataObject = System.Windows.DataObject;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DragEventArgs = System.Windows.DragEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
