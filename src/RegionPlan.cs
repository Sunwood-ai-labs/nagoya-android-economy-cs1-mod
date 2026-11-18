using System;

namespace NagoyaRoadDelivery
{
    public sealed class RegionPlan
    {
        public int Id;
        public int Hub;
        public int[] Targets;
        public string Name;

        public RegionPlan() { Targets = new int[0]; Name = ""; }
        public RegionPlan(int id, int hub, int[] targets, string name)
        {
            Id = id; Hub = hub; Targets = targets ?? new int[0]; Name = name ?? "";
        }
    }

    public interface ICitywideSurfaceVisuals
    {
        void PlaceActorOnSurface(object actor, CitywideRouting.Point2 point, float headingDegrees, bool cargo, double meters, int edgeIndex);
    }

    public interface ICitywideVisuals
    {
        object CreateActor(string kind, int id);
        void PlaceActor(object actor, CitywideRouting.Point2 point, float headingDegrees, bool cargo, double meters);
        void SetCharger(int region, bool occupied);
    }
}
