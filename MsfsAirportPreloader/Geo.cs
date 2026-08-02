using System;

namespace MsfsAirportPreloader
{
    /// <summary>Great-circle distance helpers.</summary>
    internal static class Geo
    {
        private const double EarthRadiusNauticalMiles = 3440.065;

        /// <summary>Haversine distance in nautical miles between two lat/lon points (degrees).</summary>
        public static double DistanceNauticalMiles(double lat1, double lon1, double lat2, double lon2)
        {
            double lat1Radians = DegreesToRadians(lat1);
            double lat2Radians = DegreesToRadians(lat2);
            double deltaLatRadians = DegreesToRadians(lat2 - lat1);
            double deltaLonRadians = DegreesToRadians(lon2 - lon1);

            double a = Math.Sin(deltaLatRadians / 2) * Math.Sin(deltaLatRadians / 2) +
                       Math.Cos(lat1Radians) * Math.Cos(lat2Radians) *
                       Math.Sin(deltaLonRadians / 2) * Math.Sin(deltaLonRadians / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return EarthRadiusNauticalMiles * c;
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
    }
}
