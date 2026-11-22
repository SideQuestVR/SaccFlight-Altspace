using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>Graph-writer helpers used by the dial functions and weapons.</summary>
    public sealed partial class GraphWriter
    {
        /// <summary>Whether `owner` has an Object variable called `name` (a stock "Is Variable Defined" unit).</summary>
        public Val IsDefined(Val owner, string name)
        {
            var unit = Add(new IsVariableDefined { kind = VariableKind.Object });
            Feed(unit, "name", name);
            Feed(unit, "object", owner);
            return Out(unit, "isDefined", typeof(bool));
        }

        /// <summary>list.Add(item), as a step (a stock "Add List Item" unit).</summary>
        public void ListAdd(Val list, Val item)
        {
            var unit = Add(new AddListItem());
            Feed(unit, "listInput", list);
            Feed(unit, "item", item);
            Chain(unit, "enter", "exit");
        }

        /// <summary>list.Clear(), as a step.</summary>
        public void ListClear(Val list)
        {
            var unit = Add(new ClearList());
            Feed(unit, "listInput", list);
            Chain(unit, "enter", "exit");
        }

        static readonly Type[] OneString = { typeof(string) };
        static readonly Type[] TwoStrings = { typeof(string), typeof(string) };
        static readonly Type[] ThreeStrings = { typeof(string), typeof(string), typeof(string) };

        public Val StartsWith(object text, object prefix) => Call(typeof(string), "StartsWith", OneString, text as Val ?? throw new ArgumentException("text must be a value"), prefix);
        public Val Concat(object a, object b) => Call(typeof(string), "Concat", TwoStrings, null, a, b);
        public Val Concat(object a, object b, object c) => Call(typeof(string), "Concat", ThreeStrings, null, a, b, c);

        /// <summary>text.Split(separator) as a string array.</summary>
        public Val Split(Val text, string separator) =>
            Call(typeof(string), "Split", new[] { typeof(string), typeof(StringSplitOptions) }, text, separator, StringSplitOptions.None);

        /// <summary>A number sent in a one-shot: hundredths as a whole number, so no locale can put a comma in it.</summary>
        public Val NumberText(object value) =>
            Call(typeof(int), "ToString", Type.EmptyTypes, Call(typeof(Mathf), "RoundToInt", new[] { typeof(float) }, null, value is Val v ? v * 100f : (object)((float)value * 100f)));

        /// <summary>The number NumberText wrote.</summary>
        public Val ParseNumber(object text) =>
            PlatformValue("ParseStringInvariant", "float", typeof(float), ("string", text)) / 100f;

        public Val RandomValue() => Get(typeof(UnityEngine.Random), "value");

        /// <summary>An explicit null of a type (a call input left unconnected has no value at all).</summary>
        public Val Null(Type type) => Out(Add(new Literal(type, null)), "output", type);
    }
}
