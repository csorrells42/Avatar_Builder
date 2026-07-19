using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public interface IFaceCueAnalyzer
{
    void Reset();

    FaceCueBaselineSnapshot ExportBaseline();

    bool TryImportBaseline(FaceCueBaselineSnapshot? baseline);

    FaceCueAnalysis Analyze(BitmapSource bitmap, FaceCueGuideLayout layout);
}
