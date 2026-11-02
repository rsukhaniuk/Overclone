// Fixture input only: freeze wall-clock time before Emscripten initializes.
// Monotonic performance.now() and worker settlement remain real. No resulting
// PDF, DVI, raw map or log bytes are filtered, rewritten or normalized.
const RealDate=Date;
const fixtureMilliseconds=1780000000000;
globalThis.Date=class FixtureDate extends RealDate{
 constructor(...args){super(...(args.length?args:[fixtureMilliseconds]));}
 static now(){return fixtureMilliseconds;}
};
