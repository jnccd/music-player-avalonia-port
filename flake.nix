{
  description = "Nix Dev shell for Avalonia .NET Desktop development";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
    numtide-utils = {
      url = "github:numtide/flake-utils";
    };
    jnccd-utils = {
      url = "github:jnccd/nix-utils";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    { self, nixpkgs, ... }@inputs:
    (inputs.numtide-utils.lib.eachSystem [ "x86_64-linux" "aarch64-linux" ] (
      system:
      let
        pkgs = import nixpkgs { inherit system; };
        workloadsHashX86_64Linux = "sha256-AcfemNC9S9Lk9AeW+EokaKYJpf3aDGywMTsi821Mo9M=";
      in
      {
        devShells = rec {
          desktop = inputs.jnccd-utils.lib.mkDotnetWithWorkloadsShell {
            inherit system nixpkgs;
            dotnetVersion = "10.0";
            includeAndroidSdk = false;

            additionalPackages = [
              pkgs.pulseaudio
              pkgs.yt-dlp
            ];

            workloadsHash = workloadsHashX86_64Linux;
          };

          dev = inputs.jnccd-utils.lib.mkDotnetWithWorkloadsShell {
            inherit system nixpkgs;
            dotnetVersion = "10.0";
            includeAndroidSdk = false;

            additionalPackages = [
              pkgs.pulseaudio
              pkgs.yt-dlp
            ];
            extraShellHook = ''
              export LD_LIBRARY_PATH="${pkgs.pulseaudio}/lib/:$LD_LIBRARY_PATH"
              export PULSE_SERVER=unix:/run/user/$(id -u)/pulse/native
            '';

            workloadsHash = workloadsHashX86_64Linux;
          };

          default = dev;
        };
      }
    ));
}
