using System.Diagnostics.CodeAnalysis;

// This package is a separate world implementation, not an extension of Paradise.ECS: its World does not
// implement IWorld, so schedules, tag/managed worlds and snapshots cannot use it, and its structural
// operations have known races (issue #349). Consumers must opt in until it is rebuilt over the core
// world's storage or withdrawn.
[assembly: Experimental("PECS9000", UrlFormat = "https://github.com/ParadiseEngine/ParadiseEngine/issues/366")]
