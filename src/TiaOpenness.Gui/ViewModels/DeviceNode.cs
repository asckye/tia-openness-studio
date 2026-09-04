using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Gui.Common;

namespace TiaOpenness.Gui.ViewModels;

/// <summary>
/// One row of the device tree: the project at the root, the device groups the engineer filed
/// devices into, and the devices themselves — the shape TIA's own project tree has.
///
/// A flat list loses the grouping, and on a real plant that grouping is how people find anything.
/// </summary>
public sealed class DeviceNode : ObservableObject
{
    private bool _isExpanded = true;

    private DeviceNode(string name, DeviceInfo? device, bool isRoot = false)
    {
        Name = name;
        Device = device;
        IsRoot = isRoot;
    }

    public string Name { get; }

    /// <summary>The device this row stands for, or null for the project root and for groups.</summary>
    public DeviceInfo? Device { get; }

    public bool IsRoot { get; }

    public ObservableCollection<DeviceNode> Children { get; } = [];

    public bool IsFolder => Device is null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>What TIA shows under the name: the order number for a device, a count for a group.</summary>
    public string Detail => Device is not null
        ? Device.ArticleNumber ?? string.Empty
        : "(" + Devices().Count() + ")";

    /// <summary>"Plc", "Hmi", "Drive" or "Other"; empty for the root and for groups.</summary>
    public string Category => Device?.Category ?? string.Empty;

    private IEnumerable<DeviceInfo> Devices()
    {
        if (Device is not null)
        {
            yield return Device;
            yield break;
        }
        foreach (var device in Children.SelectMany(c => c.Devices())) yield return device;
    }

    /// <summary>
    /// Builds the tree. <paramref name="projectName"/> is the root, as in TIA; when nothing is
    /// open there is no root and the caller shows its empty state instead.
    /// </summary>
    public static IReadOnlyList<DeviceNode> Build(IEnumerable<DeviceInfo> devices, string projectName)
    {
        var list = devices.ToList();
        if (list.Count == 0) return [];

        var root = new DeviceNode(
            string.IsNullOrWhiteSpace(projectName) ? "—" : projectName, null, isRoot: true);

        foreach (var device in list) Place(root, device);
        Sort(root);
        return [root];
    }

    private static void Place(DeviceNode root, DeviceInfo device)
    {
        var parent = root;

        if (!string.IsNullOrEmpty(device.GroupPath))
        {
            foreach (var name in device.GroupPath.Split('/'))
            {
                var group = parent.Children.FirstOrDefault(c => c.IsFolder && c.Name == name);
                if (group is null)
                {
                    group = new DeviceNode(name, null);
                    parent.Children.Add(group);
                }
                parent = group;
            }
        }

        parent.Children.Add(new DeviceNode(device.Name, device));
    }

    /// <summary>Groups before devices, each alphabetical — the order TIA uses.</summary>
    private static void Sort(DeviceNode node)
    {
        var ordered = node.Children
            .OrderBy(c => c.IsFolder ? 0 : 1)
            .ThenBy(c => c.Name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in ordered)
        {
            node.Children.Add(child);
            Sort(child);
        }
    }
}
