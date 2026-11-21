using Common.Utils.VDL.Data;

namespace Common.Utils.VDL.OS;

internal class ProcessTree
{
	public List<MultiTreeNode<PData>> Tree { get; set; }

	public ProcessTree(bool fetchModulePaths)
	{
		Tree = GetTree(fetchModulePaths);
	}

	public void Walk(
		Action<MultiTreeNode<PData>>? pre,
		Action<MultiTreeNode<PData>>? post = null)
	{
		Tree.ForEach(p => p.DFSWalk(pre, post));
	}

	public static List<MultiTreeNode<PData>> GetTree(bool fetchModulePaths)
	{
		var procList = SystemProcesses.GetProcesses(fetchModulePaths);

		// top-level process 'forest'
		var roots = new List<MultiTreeNode<PData>>();

		var procMap = new Dictionary<uint, MultiTreeNode<PData>>();
		foreach (var p in procList)
		{
			procMap.Add(p.Id, new MultiTreeNode<PData>(p));
		}

		foreach (var entry in procMap)
		{
			var id = entry.Key;
			var procNode = entry.Value;
			var proc = procNode.NodeValue;
			bool parentIsValid = false;

			if (procMap.TryGetValue(proc.ParentId, out var parentNode))
			{
				parentIsValid = true;
				var parentProc = parentNode.NodeValue;

				if (parentProc?.CreationTime != null && proc.CreationTime != null)
				{
					// check whether the parent's PID was reused
					parentIsValid = parentProc.CreationTime?.Ticks <= proc.CreationTime?.Ticks;
				}
			}

			if (parentIsValid
				&& proc.Id != proc.ParentId) // avoid self-referencing (like PID 0 or System)
			{
				parentNode?.AddChild(procNode);
			}
			else
			{
				roots.Add(procNode);
			}
		}

		return roots;
	}

	public void Print()
	{
		const string Cross = " ├─",
					 Corner = " └─",
					 Vertical = " │ ",
					 Space = "   ";

		int lastDepth = 0;
		var prefixes = new LinkedList<string>();
		bool prevIsLastChild = false;

		Walk(node =>
		{
			var p = node.NodeValue;

			int depth = node.Depth;

			var isLastChild = node.Parent == null
						   || node.Parent.Children.Last().NodeValue.Id == p.Id;

			// stepped down
			for (int i = lastDepth; i < depth; i++)
			{
				prefixes.AddLast(prevIsLastChild ? Space : Vertical);
			}

			// stepped up
			for (int i = depth; i < lastDepth; i++)
			{
				prefixes.RemoveLast();
			}

			var mainPrefix = string.Join("", prefixes);

			var prefix2 = depth == 0 ? node.Children.Any() ? Space : String.Empty :
				isLastChild ? Corner : Cross;

			var modulePath = SystemProcesses.GetProcessPath(p.Id);
			var moduleStr = !String.IsNullOrWhiteSpace(modulePath) ? $" ({modulePath}) " : String.Empty;

			var text = $"{(depth == 0 ? "\nProcess:" : "Subprocess:")} (Name: {p.Name}){moduleStr}(PID: {p.Id})";
			Console.WriteLine($"{mainPrefix}{prefix2}{text}");

			var windows = SystemWindows.GetWindowsData(p.Id);

			if (windows.Count() > 0)
			{
				if (depth == 0)
				{
					prefix2 = node.Children.Any() ? Space + Vertical : Space;
				}
				else
				{
					if (node.Children.Any())
					{
						prefix2 = isLastChild ? Space + Vertical : Vertical + Vertical;
					}
					else
					{
						prefix2 = isLastChild ? Space : Vertical;
					}
				}

				foreach (WData w in windows)
				{
					string flags = String.Empty;
					flags += $"{(w.IsVisible ? " [visible]" : "")}";
					flags += $"{(w.IsCloaked ? " [cloaked]" : "")}";

					// don't show invisible, untitled windows
					if (!(String.IsNullOrWhiteSpace(w.Title) && !w.IsVisible))
					{
						Console.WriteLine($"{mainPrefix}{prefix2}Window: \"{w.Title}\"{flags}");
					}
				}
			}

			lastDepth = node.Depth;
			prevIsLastChild = isLastChild;
		});
	}
}
