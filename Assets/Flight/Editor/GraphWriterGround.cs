using System;
using Unity.VisualScripting;
using UnityEngine;

namespace FlightWorld.FlightGraphs
{
    /// <summary>Graph-writer helpers used by the ground-vehicle graphs.</summary>
    public sealed partial class GraphWriter
    {
        /// <summary>list[index] (a stock "Get List Item" unit).</summary>
        public Val ListItem(Val list, object index, Type type)
        {
            var unit = Add(new GetListItem());
            Feed(unit, "list", list);
            Feed(unit, "index", index);
            return Out(unit, "item", type);
        }

        static readonly Type V3Type = typeof(Vector3);
        static readonly Type[] TwoVectors = { typeof(Vector3), typeof(Vector3) };

        public Val ProjectOnPlane(object vector, object planeNormal) => Call(V3Type, "ProjectOnPlane", TwoVectors, null, vector, planeNormal);
        public Val Project(object vector, object onNormal) => Call(V3Type, "Project", TwoVectors, null, vector, onNormal);
        public Val Normalized(Val vector) => Get(V3Type, "normalized", vector);
        public Val Angle(object from, object to) => Call(V3Type, "Angle", TwoVectors, null, from, to);
        public Val ClampMagnitude(Val vector, object maxLength) =>
            Call(V3Type, "ClampMagnitude", new[] { typeof(Vector3), typeof(float) }, null, vector, maxLength);

        /// <summary>Vector3.Lerp with literal or computed ends (t is clamped to 0..1).</summary>
        public Val LerpVector(object a, object b, object t) =>
            Call(V3Type, "Lerp", new[] { typeof(Vector3), typeof(Vector3), typeof(float) }, null, a, b, t);

        /// <summary>new Vector2(x, y).</summary>
        public Val V2(object x, object y)
        {
            var ctor = typeof(Vector2).GetConstructor(new[] { typeof(float), typeof(float) });
            var unit = Add(new InvokeMember(Use(new Member(typeof(Vector2), ctor))));
            Feed(unit, unit.inputParameters[0].key, x);
            Feed(unit, unit.inputParameters[1].key, y);
            return Out(unit, "result", typeof(Vector2));
        }

        public Val Sign(object value) => Call(typeof(Mathf), "Sign", new[] { typeof(float) }, null, value);
        public Val RoundToInt(object value) => Call(typeof(Mathf), "RoundToInt", new[] { typeof(float) }, null, value);
    }
}
