<?
if ($_SESSION[ADMINID]) {
?>

<div id="content">
	<h2><?= SYSNAME ?>: <? echo $webui_admin_home ?></h2>
	<? echo $webui_welcome_userid; ?>
	<h1><? echo $webui_admin_welcome; ?> <? echo $webui_admin_panel; ?> <?= SYSNAME ?></h1>
	<div id="info">
		<p><? echo $webui_admin_home_info; ?></p>
	</div>
	<div>
	<p>
<?
	$DbLink2 = new DB;
	$DbLink = new DB;
	if ($_SESSION[USERID])
		$Display = 1;
	else
		$Display = 0;

	if($_SESSION[ADMINID])
		$AdminDisplay = " or (display='3')";
	else
		$AdminDisplay = "";
	$DbLink2->query("SELECT id,url,target FROM " . C_PAGE_TBL . " Where parent = '".cleanQuery($_GET[btn])."' and active='1' and ((display='$Display') or (display='2') " . $AdminDisplay . ") ORDER BY rank ASC ");
	$a = get_defined_vars();
	while (list($siteid, $siteurl, $sitetarget) = $DbLink2->next_record()) 
	{
		echo "<a href=\"$siteurl&btn=$siteid\"><span>$a[$siteid]</span></a><br/>";
	}
?>
	</p>
	</div>
</div>

  <? } else { ?>
	<div id="content">  
		<h2><?= SYSNAME ?>: <? echo $webui_admin_login ?></h2>      
		<div id="login">        
			<form action="index.php" method="POST" onsubmit="if (!validate(this)) return false;">
				<table>
					<tr><td class="error" colspan="2" align="center" id="error_message"><?=$_SESSION[ERROR];$_SESSION[ERROR]="";?><?=$_GET[ERROR]?></td></tr>
					<tr>
						<td class="odd"><span id="logname_label"><? echo $webui_user_name ?>*</span></td>
						<td class="odd"><input require="true" label="logname_label" id="login_input" name="logname" type="text" value="<?= $_POST[logname] ?>" /></td>
					</tr>
					<tr>
						<td class="even"><span id="password_label"><? echo $webui_password ?>*</span></td>
						<td class="even"><input require="true" label="password_label" id="login_input" type="password" name="logpassword" /></td>
					</tr>
					<tr>
						<td class="odd"><a href="index.php?page=forgotpass"><? echo $webui_forgot_password ?></a></td>
						<td class="odd"><input id="login_bouton" type="submit" name="Submit" value="<? echo $webui_admin_login ?>" /></td>
					</tr>
				</table>
			</form>
		</div>
	</div>
  <? } ?>

