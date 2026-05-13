using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace superClipboard
{
    /// <summary>
    /// HomePage - 首页
    /// </summary>
    public partial class HomePage : UserControl
    {
        private readonly LocalizationService _loc;

        public HomePage()
        {
            InitializeComponent();
            _loc = LocalizationService.Instance;
            ApplyLocalization();
        }

        private void ApplyLocalization()
        {
            HomeTitle.Text = _loc["app.title"];
            HomeSubtitle.Text = _loc["home.welcome"];
        }
    }
}
