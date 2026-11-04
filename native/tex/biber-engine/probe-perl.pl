use strict;
use warnings;
use Config;
use JSON::PP;

# Run in the existing trusted build image, with networking disabled. This probes
# native build tools only; a successful module load is never a WASM capability.
my @modules = qw(Biber Biber::Config Module::Build XML::LibXML XML::LibXSLT
    Unicode::Normalize Unicode::Collate Text::BibTeX Text::CSV_XS
    Unicode::LineBreak Unicode::GCString Sort::Key Encode);
my @rows;
for my $module (@modules) {
    my $ok = eval "require $module; 1";
    my $version = $ok ? eval { $module->VERSION() } : undef;
    push @rows, {
        module => $module,
        available => $ok ? JSON::PP::true : JSON::PP::false,
        version => defined($version) ? "$version" : undef,
    };
}
print JSON::PP->new->canonical->pretty->encode({
    schemaVersion => 1,
    runtime => 'native-host-toolchain-only',
    perlVersion => "$^V",
    arch => $Config{archname},
    modules => \@rows,
});
