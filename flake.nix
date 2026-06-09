{
  description = "Nix Dev shell for Avalonia .NET Desktop development";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.11";
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
      in
      {
        devShells = rec {
          desktop = inputs.jnccd-utils.lib.mkUnfrozenDotnetShell {
            inherit system nixpkgs;
            dotnetVersion = "10.0";
            includeAndroidSdk = false;

            command = "cd music-player-avalonia-port ; bash ./start_desktop_app.sh";
          };

          dev = inputs.jnccd-utils.lib.mkUnfrozenDotnetShell {
            inherit system nixpkgs;
            dotnetVersion = "10.0";
            includeAndroidSdk = false;
          };

          default = dev;
        };
      }
    ));
}
