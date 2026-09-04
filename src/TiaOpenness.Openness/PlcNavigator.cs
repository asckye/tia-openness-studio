using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using TiaOpenness.Contracts.Models;

namespace TiaOpenness.Openness
{
    /// <summary>
    /// One PLC's software, plus flat lookups keyed by the slash-separated path the wire
    /// protocol uses. The Openness object model is a tree of compositions with no addressing
    /// scheme of its own, so every call would otherwise re-walk it.
    /// </summary>
    internal sealed class PlcContext
    {
        public string DeviceId;
        public PlcSoftware Software;

        public readonly Dictionary<string, PlcBlock> Blocks =
            new Dictionary<string, PlcBlock>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, PlcType> Types =
            new Dictionary<string, PlcType>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<string, PlcTagTable> TagTables =
            new Dictionary<string, PlcTagTable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Group that owns each block, so an export can recreate the folder layout.</summary>
        public readonly Dictionary<string, string> Folders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Walks the project tree and turns Openness objects into wire DTOs.</summary>
    internal static class PlcNavigator
    {
        // ---- devices -------------------------------------------------------

        /// <summary>Every device in the project, including those nested in device groups.</summary>
        public static IEnumerable<Device> AllDevices(Project project)
        {
            return AllDeviceEntries(project).Select(e => e.Device);
        }

        /// <summary>A device together with the device groups it sits in, as TIA files them.</summary>
        public struct DeviceEntry
        {
            public Device Device;
            /// <summary>Slash-separated group names; empty at the project root.</summary>
            public string GroupPath;
        }

        public static IEnumerable<DeviceEntry> AllDeviceEntries(Project project)
        {
            foreach (var device in project.Devices.SafeEnumerate())
            {
                yield return new DeviceEntry { Device = device, GroupPath = string.Empty };
            }

            foreach (var group in project.DeviceGroups.SafeEnumerate())
            {
                foreach (var entry in AllDeviceEntries(group, group.Name)) yield return entry;
            }
        }

        private static IEnumerable<DeviceEntry> AllDeviceEntries(DeviceUserGroup group, string path)
        {
            foreach (var device in group.Devices.SafeEnumerate())
            {
                yield return new DeviceEntry { Device = device, GroupPath = path };
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                foreach (var entry in AllDeviceEntries(child, Join(path, child.Name))) yield return entry;
            }
        }

        public static DeviceInfo Describe(DeviceEntry entry)
        {
            var device = entry.Device;

            // One walk, not three. Every step into DeviceItems and every GetService is a COM
            // round trip, and a rack with a few dozen items made describing one device cost
            // hundreds of them - separately for the software, for the item carrying it, and again
            // for the item names.
            var items = AllDeviceItems(device).ToList();
            var carrier = items.FirstOrDefault(i => SoftwareOf(i) != null);
            var software = carrier == null ? null : SoftwareOf(carrier);
            var display = carrier ?? HeadModule(items, device);

            return new DeviceInfo
            {
                Id = device.Name,
                Name = device.Name,
                DisplayName = display?.Name ?? device.Name,
                TypeName = display?.Attr<string>("TypeName") ?? device.Attr<string>("TypeName"),
                TypeIdentifier = device.TypeIdentifier,
                ArticleNumber = (carrier ?? items.FirstOrDefault())?.Attr<string>("OrderNumber")
                                ?? device.Attr<string>("OrderNumber"),
                FirmwareVersion = (carrier ?? items.FirstOrDefault())?.Attr<string>("FirmwareVersion"),
                Category = Categorize(software),
                GroupPath = entry.GroupPath ?? string.Empty,
                ItemNames = items.Select(i => i.Name).ToList(),
            };
        }

        /// <summary>
        /// The module whose name TIA shows for a device that carries no software — a switch, a
        /// drive. TIA prints "SW_701 [SCALANCE X208]" where the station itself is only called
        /// "SCALANCE X-200", so the name worth showing belongs to the head module, not the station.
        ///
        /// <see cref="AllDeviceItems(Device)"/> yields each top-level item before descending, so
        /// the first entry is the head module. A station whose only item repeats the station's own
        /// name tells us nothing, and falls back to the station.
        /// </summary>
        private static DeviceItem HeadModule(IReadOnlyList<DeviceItem> items, Device device)
        {
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Name)) continue;
                if (string.Equals(item.Name, device.Name, StringComparison.OrdinalIgnoreCase)) continue;
                return item;
            }
            return null;
        }

        private static Software SoftwareOf(DeviceItem item)
        {
            try { return item.GetService<SoftwareContainer>()?.Software; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Decided from the software type name rather than a typed check, so the adapter needs
        /// only Siemens.Engineering.dll &#8212; HmiTarget lives in Siemens.Engineering.Hmi.dll.
        /// </summary>
        private static string Categorize(Software software)
        {
            if (software == null) return "Other";
            if (software is PlcSoftware) return "Plc";

            var name = software.GetType().Name;
            if (name.IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) >= 0) return "Hmi";
            if (name.IndexOf("Drive", StringComparison.OrdinalIgnoreCase) >= 0) return "Drive";
            return "Other";
        }

        /// <summary>Finds the PLC software behind a device, descending into nested device items.</summary>
        public static PlcSoftware FindPlcSoftware(Device device)
        {
            return FindSoftware(device) as PlcSoftware;
        }

        public static Software FindSoftware(Device device)
        {
            foreach (var item in AllDeviceItems(device))
            {
                var software = SoftwareOf(item);
                if (software != null) return software;
            }
            return null;
        }

        public static IEnumerable<DeviceItem> AllDeviceItems(Device device)
        {
            foreach (var item in device.DeviceItems.SafeEnumerate())
            {
                yield return item;
                foreach (var nested in AllDeviceItems(item)) yield return nested;
            }
        }

        private static IEnumerable<DeviceItem> AllDeviceItems(DeviceItem item)
        {
            foreach (var child in item.DeviceItems.SafeEnumerate())
            {
                yield return child;
                foreach (var nested in AllDeviceItems(child)) yield return nested;
            }
        }

        // ---- blocks --------------------------------------------------------

        /// <summary>Fills <paramref name="context"/> with every block, UDT and tag table.</summary>
        public static void Index(PlcContext context)
        {
            context.Blocks.Clear();
            context.Types.Clear();
            context.TagTables.Clear();
            context.Folders.Clear();

            IndexBlockGroup(context, context.Software.BlockGroup, string.Empty);
            IndexTypeGroup(context, context.Software.TypeGroup, string.Empty);
            IndexTagTableGroup(context, context.Software.TagTableGroup, string.Empty);
        }

        private static void IndexBlockGroup(PlcContext context, PlcBlockGroup group, string prefix)
        {
            foreach (var block in group.Blocks.SafeEnumerate())
            {
                var path = Join(prefix, block.Name);
                context.Blocks[path] = block;
                context.Folders[path] = prefix;
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                IndexBlockGroup(context, child, Join(prefix, child.Name));
            }
        }

        private static void IndexTypeGroup(PlcContext context, PlcTypeGroup group, string prefix)
        {
            foreach (var type in group.Types.SafeEnumerate())
            {
                var path = Join(prefix, type.Name);
                context.Types[path] = type;
                context.Folders[path] = prefix;
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                IndexTypeGroup(context, child, Join(prefix, child.Name));
            }
        }

        private static void IndexTagTableGroup(PlcContext context, PlcTagTableGroup group, string prefix)
        {
            foreach (var table in group.TagTables.SafeEnumerate())
            {
                var path = Join(prefix, table.Name);
                context.TagTables[path] = table;
                context.Folders[path] = prefix;
            }

            foreach (var child in group.Groups.SafeEnumerate())
            {
                IndexTagTableGroup(context, child, Join(prefix, child.Name));
            }
        }

        public static BlockInfo Describe(PlcBlock block, string path)
        {
            var db = block as DataBlock;
            var instanceOf = db?.Prop<string>("InstanceOfName");

            return new BlockInfo
            {
                Path = path,
                Name = block.Name,
                Kind = KindOf(block, instanceOf),
                Number = block.Prop<int?>("Number"),
                ProgrammingLanguage = block.Prop<string>("ProgrammingLanguage"),
                IsConsistent = block.Prop("IsConsistent", true),
                IsKnowHowProtected = block.Prop("IsKnowHowProtected", false),
                ModifiedDate = block.Prop<DateTime>("ModifiedDate").AsOffset(),
                HeaderAuthor = block.Attr<string>("HeaderAuthor"),
                HeaderVersion = block.Attr<string>("HeaderVersion"),
            };
        }

        public static BlockInfo Describe(PlcType type, string path)
        {
            return new BlockInfo
            {
                Path = path,
                Name = type.Name,
                Kind = BlockKind.UDT,
                Number = null,
                ProgrammingLanguage = null,
                IsConsistent = type.Prop("IsConsistent", true),
                IsKnowHowProtected = type.Prop("IsKnowHowProtected", false),
                ModifiedDate = type.Prop<DateTime>("ModifiedDate").AsOffset(),
                HeaderAuthor = type.Attr<string>("HeaderAuthor"),
                HeaderVersion = type.Attr<string>("HeaderVersion"),
            };
        }

        private static BlockKind KindOf(PlcBlock block, string instanceOfName)
        {
            if (block is OB) return BlockKind.OB;
            if (block is FB) return BlockKind.FB;
            if (block is FC) return BlockKind.FC;
            if (block is DataBlock)
            {
                return string.IsNullOrWhiteSpace(instanceOfName) ? BlockKind.DB : BlockKind.InstanceDB;
            }
            return BlockKind.Unknown;
        }

        // ---- tags ----------------------------------------------------------

        public static TagTableInfo Describe(PlcTagTable table, string path)
        {
            var count = 0;
            try { count = table.Tags.Count; } catch (Exception) { count = 0; }

            return new TagTableInfo
            {
                Path = path,
                Name = table.Name,
                TagCount = count,
                IsDefault = string.Equals(table.Name, "Default tag table", StringComparison.OrdinalIgnoreCase),
            };
        }

        public static TagInfo Describe(PlcTag tag, string tableName)
        {
            return new TagInfo
            {
                Name = tag.Name,
                DataType = tag.Prop<string>("DataTypeName"),
                LogicalAddress = tag.Prop<string>("LogicalAddress"),
                Comment = (tag.Prop<object>("Comment") as MultilingualText).AsText(),
                TableName = tableName,
            };
        }

        // ---- helpers -------------------------------------------------------

        public static string Join(string prefix, string name)
        {
            return string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;
        }
    }
}
