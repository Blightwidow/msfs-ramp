using MsfsAirportPreloader;
using Xunit;

namespace MsfsAirportPreloader.Tests
{
    public class GeoTests
    {
        [Fact]
        public void DistanceToSamePointIsZero()
        {
            Assert.Equal(0.0, Geo.DistanceNauticalMiles(49.0097, 2.5479, 49.0097, 2.5479), 6);
        }

        [Fact]
        public void OneDegreeOfLatitudeIsSixtyNauticalMiles()
        {
            Assert.Equal(60.0, Geo.DistanceNauticalMiles(45.0, 5.0, 46.0, 5.0), 0);
        }

        [Fact]
        public void ParisCdgToOrlyIsAboutEighteenNauticalMiles()
        {
            double distance = Geo.DistanceNauticalMiles(49.0097, 2.5479, 48.7233, 2.3794);
            Assert.InRange(distance, 18.0, 19.0);
        }
    }
}
