#import "MixedSpaceAppleLocationBridge.h"

@interface MixedSpaceAppleLocationDriver ()

@property(nonatomic, strong) CLLocationManager *manager;
@property(nonatomic, strong) CLLocation *latestSample;
@property(nonatomic, readwrite) BOOL hasUsableSample;

@end

@implementation MixedSpaceAppleLocationDriver

- (instancetype)init
{
    self = [super init];
    if (self == nil)
        return nil;

    _manager = [[CLLocationManager alloc] init];
    _manager.delegate = self;
    _manager.distanceFilter = kCLDistanceFilterNone;
    _manager.desiredAccuracy = kCLLocationAccuracyBest;
    [_manager requestWhenInUseAuthorization];
    return self;
}

- (void)beginUpdates
{
    [self.manager startUpdatingLocation];
}

- (void)endUpdates
{
    [self.manager stopUpdatingLocation];
    self.hasUsableSample = NO;
}

- (void)locationManager:(CLLocationManager *)manager
      didUpdateLocations:(NSArray<CLLocation *> *)locations
{
    CLLocation *sample = locations.lastObject;
    if (sample == nil)
        return;

    self.latestSample = sample;
    self.hasUsableSample = YES;
}

- (void)locationManager:(CLLocationManager *)manager didFailWithError:(NSError *)error
{
    self.hasUsableSample = NO;
}

- (void)locationManagerDidChangeAuthorization:(CLLocationManager *)manager
{
    CLAuthorizationStatus status;
    if (@available(iOS 14.0, *))
        status = manager.authorizationStatus;
    else
        status = [CLLocationManager authorizationStatus];

    if (status == kCLAuthorizationStatusDenied ||
        status == kCLAuthorizationStatusRestricted)
    {
        self.hasUsableSample = NO;
    }
}

- (double)latitude { return self.latestSample.coordinate.latitude; }
- (double)longitude { return self.latestSample.coordinate.longitude; }
- (double)altitude { return self.latestSample.altitude; }
- (double)horizontalAccuracy { return self.latestSample.horizontalAccuracy; }
- (double)verticalAccuracy { return self.latestSample.verticalAccuracy; }

@end

static MixedSpaceAppleLocationDriver *s_locationDriver = nil;

static MixedSpaceAppleLocationDriver *GetLocationDriver(void)
{
    if (s_locationDriver == nil)
        s_locationDriver = [[MixedSpaceAppleLocationDriver alloc] init];
    return s_locationDriver;
}

extern "C"
{
    void startLocation(void) { [GetLocationDriver() beginUpdates]; }

    void stopLocation(void)
    {
        if (s_locationDriver != nil)
            [s_locationDriver endUpdates];
    }

    // Reads must not instantiate CLLocationManager or request permission.
    // Messaging a nil driver returns zero for these scalar return types.
    double getLatitude(void) { return [s_locationDriver latitude]; }
    double getLongitude(void) { return [s_locationDriver longitude]; }
    double getAltitude(void) { return [s_locationDriver altitude]; }
    double getHorizontalAccuracy(void) { return [s_locationDriver horizontalAccuracy]; }
    double getVerticalAccuracy(void) { return [s_locationDriver verticalAccuracy]; }
    bool locationServicesEnabled(void)
    {
        return s_locationDriver != nil && s_locationDriver.hasUsableSample;
    }
}
