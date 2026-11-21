using System.Collections;

namespace Common.Utils.VDL.Data;

// Simple tree with iterative DFS algo
public class MultiTreeNode<T> : IEnumerable<MultiTreeNode<T>>
{
	public T NodeValue { get; set; }
	public MultiTreeNode<T>? Parent { get; private set; } = null;
	public List<MultiTreeNode<T>> Children { get; } = new();
	public int Depth { get; private set; } = 0;

	public MultiTreeNode(T value) => NodeValue = value;

	public void AddChild(MultiTreeNode<T> node)
	{
		Children.Add(node);
		node.Parent = this;

		// update depth values of a new branch
		if (node.Depth != this.Depth + 1)
		{
			node.DFSWalk(n =>
			{
				n.Depth = n.Parent!.Depth + 1;
			});
		}
	}

	public IEnumerator<MultiTreeNode<T>> GetEnumerator()
		=> MultiTreeNodeExtensions.AsEnumerable(this).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal enum NodeColor { WHITE = 0, GRAY, BLACK };

internal static class MultiTreeNodeExtensions
{
	public static void DFSWalk<T>(this MultiTreeNode<T> root,
		Action<MultiTreeNode<T>>? preOrderAction,
		Action<MultiTreeNode<T>>? postOrderAction = null)
	{
		if (root == null)
		{
			return;
		}

		var nodeStates = new Dictionary<MultiTreeNode<T>, NodeColor>();
		var stack = new Stack<(MultiTreeNode<T> Node, IEnumerator<MultiTreeNode<T>> Neighbors)>();

		try
		{
			DFSIteratorProc(root, preOrderAction, postOrderAction, nodeStates, stack);
		}
		catch (Exception)
		{
			throw;
		}
		finally
		{
			// Cleanup remaining enumerators
			while (stack.Count > 0)
			{
				stack.Pop().Neighbors.Dispose();
			}
		}
	}

	private static void DFSIteratorProc<T>(
		this MultiTreeNode<T> root,
		Action<MultiTreeNode<T>>? preOrderAction,
		Action<MultiTreeNode<T>>? postOrderAction,
		Dictionary<MultiTreeNode<T>, NodeColor> nodeStates,
		Stack<(MultiTreeNode<T> Node, IEnumerator<MultiTreeNode<T>> Neighbors)> stack)
	{
		preOrderAction?.Invoke(root);
		nodeStates[root] = NodeColor.GRAY;
		stack.Push((root, root.Children.GetEnumerator()));

		while (stack.Count > 0)
		{
			(MultiTreeNode<T> Node, IEnumerator<MultiTreeNode<T>> Neighbors) item = stack.Peek();

			if (item.Neighbors.MoveNext())
			{
				var child = item.Neighbors.Current;
				var childState = nodeStates.GetValueOrDefault(child); // 0 (WHITE) for non-visited nodes
				if (childState == NodeColor.WHITE)
				{
					nodeStates[child] = NodeColor.GRAY;
					preOrderAction?.Invoke(child);
					stack.Push((child, child.Children.GetEnumerator()));
				}
				else if (childState == NodeColor.GRAY)
				{
					// graph cycle
				}
			}
			else
			{
				nodeStates[item.Node] = NodeColor.BLACK;
				postOrderAction?.Invoke(item.Node);
				item.Neighbors.Dispose();
				stack.Pop();
			}
		}
	}

	public static IEnumerable<MultiTreeNode<T>> AsEnumerable<T>(this MultiTreeNode<T> node)
	{
		if (node == null) yield break;

		var visited = new HashSet<MultiTreeNode<T>>();
		var stack = new Stack<IEnumerator<MultiTreeNode<T>>>();

		try
		{
			visited.Add(node);
			yield return node;
			stack.Push(node.Children.GetEnumerator());

			while (stack.Count > 0)
			{
				var current = stack.Peek();
				if (current.MoveNext())
				{
					var child = current.Current;
					if (visited.Add(child))
					{
						yield return child;
						stack.Push(child.Children.GetEnumerator());
					}
				}
				else
				{
					current.Dispose();
					stack.Pop();
				}
			}
		}
		finally
		{
			while (stack.Count > 0)
			{
				stack.Pop().Dispose();
			}
		}
	}
}
