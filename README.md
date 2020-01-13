ShareCash is a cryptocurrency with automated wealth redistribution.  This means, whenever there is a concentration of money beyond a certain threshold, say .1%, a computer is going to automatically reset everyone's accounts to have the same exact amount.  [Learn more!](https://jubicoin.io)


# Development Setup (Visual Studio 2019 Community)
1. [Install Chocolatey](https://chocolatey.org/install)
2. Install Git - Open a **NEW** PowerShell instance as an administrator and execute `choco install -y git`
3. Clone repository - Open a **NEW** PowerShell instance as an administrator and execute `git clone https://github.com/CashShareCoin/ShareCoin.git`
4. Move to cloned repository - `cd ShareCoin`
5. Allow script execution - `Set-ExecutionPolicy Bypass -Scope Process`
5. Create development setup packages - `& ./createDevPackages`
6. Run development setup - `choco install -y sharecash-dev-vs2019community -s "devops\chocoPackages;https://chocolatey.org/api/v2"`
