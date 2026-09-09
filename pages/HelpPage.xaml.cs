using System.Windows.Controls;

namespace superClipboard
{
    /// <summary>
    /// HelpPage - 功能说明页
    /// </summary>
    public partial class HelpPage : UserControl
    {
        private readonly LocalizationService _loc;

        public HelpPage()
        {
            InitializeComponent();
            _loc = LocalizationService.Instance;
            ApplyLocalization();
        }

        private void ApplyLocalization()
        {
            PageTitle.Text = _loc["help.title"];
            HelpMonitorTitle.Text = _loc["help.monitor.title"];
            HelpMonitorBody.Text = _loc["help.monitor.body"];
            HelpHistoryTitle.Text = _loc["help.history.title"];
            HelpHistoryBody.Text = _loc["help.history.body"];
            HelpPasteQueueTitle.Text = _loc["help.pastequeue.title"];
            HelpPasteQueueBody.Text = _loc["help.pastequeue.body"];
            HelpExcelTitle.Text = _loc["help.excel.title"];
            HelpExcelBody.Text = _loc["help.excel.body"];
            HelpFileSendTitle.Text = _loc["help.filesend.title"];
            HelpFileSendBody.Text = _loc["help.filesend.body"];
            HelpFileReceiveTitle.Text = _loc["help.filereceive.title"];
            HelpFileReceiveBody.Text = _loc["help.filereceive.body"];
            HelpSettingsTitle.Text = _loc["help.settings.title"];
            HelpSettingsBody.Text = _loc["help.settings.body"];
        }
    }
}
