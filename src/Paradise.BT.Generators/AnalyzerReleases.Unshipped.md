### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PBT0001 | Paradise.BT.Generators | Error | Struct with [Builder] is missing [Guid] attribute
PBT0002 | Paradise.BT.Generators | Warning | Struct with [Builder] contains managed references
PBT0003 | Paradise.BT.Design | Error | Duplicate [Guid] on INodeData structs
PBT0008 | Paradise.BT.Generators | Error | Node writes a component, which binds read-only by value
PBT0009 | Paradise.BT.Design | Error | Node uses blackboard data it does not declare
PBT0010 | Paradise.BT.Design | Warning | Blackboard passed to a method whose access cannot be checked
PBT0011 | Paradise.BT.Generators | Error | Node with [Builder] declares more than one public constructor
PBT0012 | Paradise.BT.Generators | Warning | Public field is not part of the node's constructor surface
PBT0013 | Paradise.BT.Design | Error | Builder call passes multiple value arguments positionally
