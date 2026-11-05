#import <CoreLocation/CoreLocation.h>
#import <Foundation/Foundation.h>
#include <stdbool.h>

/// Owns CLLocationManager state for the MixedSpace iOS platform adapter.
/// No geographic values are stored as process-wide C variables.
@interface MixedSpaceAppleLocationDriver : NSObject <CLLocationManagerDelegate>

@property(nonatomic, readonly) BOOL hasUsableSample;

- (void)beginUpdates;
- (void)endUpdates;
- (double)latitude;
- (double)longitude;
- (double)altitude;
- (double)horizontalAccuracy;
- (double)verticalAccuracy;

@end

#ifdef __cplusplus
extern "C" {
#endif

// Stable P/Invoke ABI. These symbols intentionally retain their shipped names.
void startLocation(void);
void stopLocation(void);
double getLatitude(void);
double getLongitude(void);
double getAltitude(void);
double getHorizontalAccuracy(void);
double getVerticalAccuracy(void);
bool locationServicesEnabled(void);

#ifdef __cplusplus
}
#endif
