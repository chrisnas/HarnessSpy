using System.Windows;
using System.Windows.Media;

namespace HarnessSpy.Wpf.Views;

public partial class AboutWindow : Window
{
    public AboutWindow(string productName, ImageSource? productImage)
    {
        InitializeComponent();
        Title = $"About {productName}";
        ProductImage.Source = productImage;
    }
}
