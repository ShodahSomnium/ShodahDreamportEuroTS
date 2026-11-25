use strict; use warnings; use JSON::PP;
my $R = shift;   # replay root (stands in for the scratchpad)
my %bashts = map { $_ => 1 } qw(
 2026-09-25T10:45:16.419Z 2026-09-25T10:48:28.068Z 2026-09-25T10:48:39.455Z 2026-09-25T10:51:40.322Z
 2026-09-25T10:51:48.559Z 2026-09-25T10:55:51.839Z 2026-09-25T10:56:03.793Z
 2026-09-25T14:39:53.547Z 2026-09-25T14:43:43.197Z 2026-09-25T14:46:31.512Z 2026-09-25T14:46:50.935Z
 2026-09-25T14:47:22.887Z 2026-09-25T14:51:45.852Z 2026-09-25T14:54:29.212Z 2026-09-25T14:56:43.944Z
 2026-09-25T15:08:57.423Z 2026-09-25T15:21:51.033Z);
my $SPre = qr{C:[\x2F\x5C]+Users[\x2F\x5C]+derii[\x2F\x5C]+AppData[\x2F\x5C]+Local[\x2F\x5C]+Temp[\x2F\x5C]+claude[\x2F\x5C]+D--Dreamport-Euro-Truck-Simulator-Euro-Truck-Sim[\x2F\x5C]+b6bed9e4-1d99-4fd7-84f3-35278f538c47[\x2F\x5C]+scratchpad}i;
my $j = JSON::PP->new;
my (@ev, %err);
for my $f (@ARGV) {
  open my $fh, '<', $f or die;
  while (my $l = <$fh>) {
    my $o = eval { $j->decode($l) } or next;
    my $c = $o->{message}{content}; next unless ref $c eq 'ARRAY';
    for my $b (@$c) {
      my $t = $b->{type} // '';
      if ($t eq 'tool_result') {
        $err{$b->{tool_use_id}} = 1 if $b->{is_error};
        my $r = $b->{content}; $r = join '', map { $_->{text} // '' } @$r if ref $r; $main::res{$b->{tool_use_id}} = $r;
        next }
      next unless $t eq 'tool_use';
      my $i = $b->{input};
      if ($b->{name} =~ /^(Write|Edit)$/) {
        my $p = $i->{file_path} // ''; $p =~ s{\x5C}{/}g;
        next unless $p =~ m{/scratchpad/(renderlab/(?!proj/).*|holes/render\.sh)$}i;
        push @ev, { ts => $o->{timestamp}, kind => $b->{name}, id => $b->{id}, rel => $1, in => $i, f => $f };
      } elsif ($b->{name} eq 'Bash' && $bashts{$o->{timestamp}}) {
        push @ev, { ts => $o->{timestamp}, kind => 'Bash', id => $b->{id}, in => $i, f => $f };
      }
    }
  }
}
@ev = sort { $a->{ts} cmp $b->{ts} } @ev;
sub rd { my $p = shift; open my $h, '<:raw', $p or return undef; local $/; my $s = <$h>; $s }
sub wr { my ($p, $s) = @_; (my $d = $p) =~ s{/[^/]*$}{}; system('mkdir', '-p', $d); open my $h, '>:raw', $p or die "$p: $!"; print $h $s; close $h }
for my $e (@ev) {
  my $tag = "$e->{ts} $e->{kind}";
  if ($err{$e->{id}}) { print "SKIP (failed originally) $tag ", ($e->{rel} // ''), "\n"; next }
  if ($e->{kind} eq 'Write') {
    my $s = $e->{in}{content}; utf8::encode($s);
    wr("$R/$e->{rel}", $s); print "$tag $e->{rel} (" . length($s) . " bytes)\n";
  } elsif ($e->{kind} eq 'Edit') {
    my $p = "$R/$e->{rel}"; my $s = rd($p); die "missing $p at $tag" unless defined $s;
    my ($o, $n) = ($e->{in}{old_string}, $e->{in}{new_string}); utf8::encode($o); utf8::encode($n);
    my $cnt = () = $s =~ /\Q$o\E/g;
    if ($cnt == 0) { print "!! EDIT NOT FOUND $tag $e->{rel}\n"; next }
    if ($e->{in}{replace_all}) { $s =~ s/\Q$o\E/$n/g } else { print "!! EDIT AMBIGUOUS ($cnt) $tag\n" if $cnt > 1; my $k = index($s, $o); substr($s, $k, length $o) = $n }
    wr($p, $s); print "$tag $e->{rel}\n";
  } else {
    my $cmd = $e->{in}{command}; utf8::encode($cmd);
    $cmd =~ s/\x5C\x5C/\x5C/g;   # the Bash tool turned every \\ into \ before bash saw it
    $cmd =~ s/$SPre/$R/g; $cmd =~ s{C:/Program Files/Unity}{/nonexistent/Unity}g;
    my $script = "bash(){ :; }; powershell(){ :; }; python3(){ return 1; }\n$cmd\n";
    open my $h, '>:raw', "$R/../cmd.sh" or die; print $h $script; close $h;
    my $out = `bash "$R/../cmd.sh" 2>&1`;
    my @l = split /\n/, $out; my $n = @l;
    print "$tag (bash, $n output lines)\n";
    print map { "     | $_\n" } grep { /No such|error|cannot|can't/i } @l;
    my $orig = $main::res{$e->{id}} // ''; utf8::encode($orig);
    open my $lg, '>>:raw', "$R/../bashlog.txt"; print $lg "===== $tag\n--- original result:\n$orig\n--- replay output:\n$out\n"; close $lg;
  }
}
