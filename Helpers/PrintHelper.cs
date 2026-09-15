using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace CelesteGallery.Helpers;

/// <summary>
/// 打印当前这张图。
///
/// 用系统自带的打印对话框（Windows.Graphics.Printing），
/// 不用往程序里塞任何第三方打印库 —— 弹出来的就是用户熟悉的那一个，
/// 能选打印机、纸张、份数、横竖版，跟在照片应用里点打印一模一样。
///
/// 打印内容的排布：整张图等比缩到纸张的"可打印区域"里居中，不裁不拉伸。
/// </summary>
public sealed class PrintHelper
{
    private readonly IntPtr _hwnd;
    private readonly ImageSource _image;
    private readonly double _aspect;

    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _source;
    private Page? _printPage;
    private Grid? _printRoot;

    public PrintHelper(IntPtr hwnd, ImageSource image, double pixelWidth, double pixelHeight)
    {
        _hwnd = hwnd;
        _image = image;
        _aspect = pixelHeight > 0 ? pixelWidth / pixelHeight : 1.0;
    }

    public async Task<bool> PrintAsync()
    {
        if (_hwnd == IntPtr.Zero) return false;

        try
        {
            _printDocument = new PrintDocument();
            _printDocument.Paginate += OnPaginate;
            _printDocument.GetPreviewPage += OnGetPreviewPage;
            _printDocument.AddPages += OnAddPages;
            _source = _printDocument.DocumentSource;

            _printManager = PrintManagerInterop.GetForWindow(_hwnd);
            _printManager.PrintTaskRequested += OnPrintTaskRequested;

            // 这行会弹出系统打印对话框（支持打印机时）
            return await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
        }
        catch
        {
            Detach();
            return false;
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        PrintTask task = args.Request.CreatePrintTask("CelesteGallery 打印图片", requested =>
        {
            // 这一句必须调用，否则打印会被取消
            requested.SetSource(_source);
        });

        // 打印结束（完成 / 取消 / 失败）都要摘掉事件，
        // 否则下次打印会重复挂上一次，页数是翻倍往上走的
        task.Completed += (_, _) => Detach();
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        try
        {
            PrintPageDescription desc = e.PrintTaskOptions.GetPageDescription(1);
            BuildPage(desc);

            _printDocument?.SetPreviewPageCount(1, PreviewPageCountType.Final);
        }
        catch
        {
            _printDocument?.SetPreviewPageCount(1, PreviewPageCountType.Final);
        }
    }

    /// <summary>
    /// 造一页：整页涂白，图片按可打印区域等比居中。
    /// 每页都重新造一次是因为纸张变了（A4 / 照片纸 / 横竖版）尺寸就不一样。
    /// </summary>
    private void BuildPage(PrintPageDescription desc)
    {
        double pageW = desc.PageSize.Width;
        double pageH = desc.PageSize.Height;

        var image = new Image
        {
            Source = _image,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var root = new Grid
        {
            Background = new SolidColorBrush(Colors.White),
        };
        root.Children.Add(image);

        var page = new Page { Content = root };

        // 可打印区域（打印机有物理边距，边上那一圈是印不上去的）
        double left = desc.ImageableRect.Left;
        double top = desc.ImageableRect.Top;
        double areaW = Math.Max(1, desc.ImageableRect.Width);
        double areaH = Math.Max(1, desc.ImageableRect.Height);

        // 等比塞进可打印区域
        double w = areaW;
        double h = w / _aspect;
        if (h > areaH)
        {
            h = areaH;
            w = h * _aspect;
        }

        image.Width = w;
        image.Height = h;

        page.Width = pageW;
        page.Height = pageH;
        root.Width = pageW;
        root.Height = pageH;
        root.Padding = new Thickness(left, top, Math.Max(0, pageW - left - areaW), Math.Max(0, pageH - top - areaH));

        _printPage = page;
        _printRoot = root;
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (_printPage is not null)
            _printDocument?.SetPreviewPage(e.PageNumber, _printPage);
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        if (_printPage is not null)
            _printDocument?.AddPage(_printPage);

        _printDocument?.AddPagesComplete();
    }

    private void Detach()
    {
        if (_printManager is not null)
        {
            _printManager.PrintTaskRequested -= OnPrintTaskRequested;
            _printManager = null;
        }

        if (_printDocument is not null)
        {
            _printDocument.Paginate -= OnPaginate;
            _printDocument.GetPreviewPage -= OnGetPreviewPage;
            _printDocument.AddPages -= OnAddPages;
            _printDocument = null;
        }

        _printPage = null;
        _printRoot = null;
        _source = null;
    }
}
