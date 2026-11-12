using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MDVTool.Controls;

/// <summary>
/// 统一矢量图标。用 24x24 线性 PathGeometry 绘制，不依赖 Segoe Fluent Icons 等系统字体，
/// 换机器不会因缺字体而显示成方块。颜色继承父级 Foreground（与原来 TextBlock 行为一致）。
/// 用法：&lt;ctrl:VectorIcon Kind="folder" Width="16" Height="16"/&gt;
/// </summary>
public class VectorIcon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(VectorIcon),
        new FrameworkPropertyMetadata(string.Empty, OnKindChanged));

    /// <summary>图标名，对应 Themes/Icons.xaml 里的 Ico.{Kind}</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static readonly DependencyPropertyKey ShapePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Shape), typeof(Geometry), typeof(VectorIcon),
            new FrameworkPropertyMetadata(Geometry.Empty));

    public static readonly DependencyProperty ShapeProperty = ShapePropertyKey.DependencyProperty;

    /// <summary>供模板绑定的几何图形</summary>
    public Geometry Shape => (Geometry)GetValue(ShapeProperty);

    public VectorIcon() => RefreshShape();

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VectorIcon)d).RefreshShape();

    private void RefreshShape()
    {
        var key = "Ico." + Kind;
        Geometry? geo = null;
        try
        {
            // 先按元素自身资源链找（支持局部覆盖），再回退到应用级资源
            geo = (TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Geometry;
        }
        catch { /* 资源字典尚未就绪时忽略，Loaded 时会再取一次 */ }
        SetValue(ShapePropertyKey, geo ?? Geometry.Empty);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (ReadLocalValue(ShapeProperty) == DependencyProperty.UnsetValue || Shape == Geometry.Empty)
            RefreshShape();
    }
}
