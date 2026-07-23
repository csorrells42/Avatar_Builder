using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed record AvatarStandardModelBuildResult(AvatarStandardModel? Model, int StoredCheckpointCount, IReadOnlyList<string> Errors, bool Cancelled);
