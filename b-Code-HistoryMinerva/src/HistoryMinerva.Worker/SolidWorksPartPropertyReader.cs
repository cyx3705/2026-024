using HistoryMinerva.Contracts;

namespace HistoryMinerva.Worker;

/// <summary>
/// V4.8：解析装配体时顺手读回每个零件的「材料」「表面处理」「热处理」现值。
///
/// **为什么在探查这一步读**：这三栏在现场大多已经填过——用户是在既有零件上补图号，
/// 不是从空白开始建库。表格开出来一片空白，用户唯一能做的就是把已有的值再选一遍；
/// 选错一格就把零件上原本正确的材质换掉了，而且属性标签上看不出任何异样。
///
/// **为什么读组件而不是打开零件**：装配已经在这个会话里打开着，每个组件的
/// <c>ModelDoc2</c> 就在内存里；再 <c>OpenDoc6</c> 一遍等于把几百个零件重开一次，
/// 探查会从几秒变成几分钟。轻化组件由 <c>ResolveLightweightComponents</c> 提前解析过。
///
/// 全程只读且不抛：任何一格读不到就留空串。属性读不到不是"装配读不出来"，
/// 不该把整次探查拖成失败——那三栏空着，用户照样能改名。
/// </summary>
internal static class SolidWorksPartPropertyReader
{
    /// <summary>
    /// 读一个零件组件的三个槽。返回 null 表示这个组件当下拿不到文档（抑制、未加载、虚拟件）。
    /// </summary>
    /// <param name="sourcePath">零件源文件全路径，作为读数在结果里的键。</param>
    public static PartPropertyReading? Read(
        SolidWorksInteropBridge interop,
        object component,
        string sourcePath)
    {
        var model = TryGet(() => interop.GetComponentModelDoc(component), (object?)null);
        if (model is null)
            return null;

        try
        {
            // 属性标签模板的控件全是 ApplyTo="Config"，读的必须与写的是同一批槽：
            // 传空串读到的是文档级「自定义」标签，那一批在本项目里永远是空的。
            var configuration = TryGet(() => interop.GetActiveConfigurationName(model), string.Empty);
            var material = TryGet(
                () =>
                {
                    var name = interop.GetMaterialPropertyName(model, configuration, out var database);
                    return (Name: name, Database: database);
                },
                (Name: string.Empty, Database: string.Empty));
            var extension = TryGet(() => interop.GetExtension(model), (object?)null);
            var surface = string.Empty;
            var heat = string.Empty;
            if (extension is not null)
            {
                var manager = TryGet(
                    () => interop.GetCustomPropertyManager(extension, configuration), (object?)null);
                if (manager is not null)
                {
                    surface = TryGet(
                        () => interop.GetCustomProperty(manager, PartPropertyNames.SurfaceTreatment),
                        string.Empty);
                    heat = TryGet(
                        () => interop.GetCustomProperty(manager, PartPropertyNames.HeatTreatment),
                        string.Empty);
                }
            }

            // 材质名脱离材料库应用不回零件，所以只有两者齐备才算读到了材料。
            var hasMaterial = material.Name.Length != 0 && material.Database.Length != 0;
            return new PartPropertyReading(
                sourcePath,
                hasMaterial ? material.Name : string.Empty,
                hasMaterial ? material.Database : string.Empty,
                surface.Trim(),
                heat.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }
}
