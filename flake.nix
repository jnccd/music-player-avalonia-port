{
  description = "Nix Dev shell for Avalonia .NET Desktop development";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-25.11";
    numtide-utils.url = {
      url = "github:numtide/flake-utils";
      inputs.nixpkgs.follows = "nixpkgs";
    };
    jnccd-utils.url = {
      url = "github:jnccd/nix-utils";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    { self, nixpkgs, ... }@inputs:
    (inputs.numtide-utils.lib.eachSystem [ "x86_64-linux" "aarch64-linux" ] (system: {
      devShells = rec {
        gui = inputs.jnccd-utils.lib.mkUnfrozenDotnetShell {
          inherit system nixpkgs;
          dotnetVersion = "10.0";
          includeAndroidSdk = false;
        };

        default = gui;
      };
    }));
}
